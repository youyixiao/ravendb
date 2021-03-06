//-----------------------------------------------------------------------
// <copyright file="MappedResults.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Isam.Esent.Interop;
using Raven.Abstractions;
using Raven.Abstractions.Extensions;
using Raven.Database.Json;
using Raven.Database.Extensions;
using Raven.Database.Storage;
using Raven.Database.Tasks;
using Raven.Json.Linq;
using System.Linq;

namespace Raven.Storage.Esent.StorageActions
{
	public partial class DocumentStorageActions : IMappedResultsStorageAction
	{
		public void PutMappedResult(string view, string docId, string reduceKey, RavenJObject data, byte[] viewAndReduceKeyHashed)
		{
	        Guid etag = uuidGenerator.CreateSequentialUuid();

			using (var update = new Update(session, MappedResults, JET_prep.Insert))
			{
				Api.SetColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["view"], view, Encoding.Unicode);
				Api.SetColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["document_key"], docId, Encoding.Unicode);
				Api.SetColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["reduce_key"], reduceKey, Encoding.Unicode);
				Api.SetColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["reduce_key_and_view_hashed"], viewAndReduceKeyHashed);
				Api.SetColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["data"], data.ToBytes());
				Api.SetColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["etag"], etag.TransformToValueForEsentSorting());
				Api.SetColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["timestamp"], SystemTime.Now);

				update.Save();
			}
		}

		public IEnumerable<RavenJObject> GetMappedResults(params GetMappedResultsParams[] getMappedResultsParams)
		{
			// optimized according to this: http://managedesent.codeplex.com/discussions/274843#post680337
			var primaryKeyIndexes = new List<Tuple<byte[], GetMappedResultsParams>>();

			Api.JetSetCurrentIndex(session, MappedResults, "by_reduce_key_and_view_hashed");
			foreach (var item in getMappedResultsParams)
			{
				Api.MakeKey(session, MappedResults, item.ViewAndReduceKeyHashed, MakeKeyGrbit.NewKey);
				if (Api.TrySeek(session, MappedResults, SeekGrbit.SeekEQ) == false)
					continue;

				var bookmarkBuffer = new byte[SystemParameters.BookmarkMost];
				var ignoredBuffer = new byte[SystemParameters.BookmarkMost];
				Api.MakeKey(session, MappedResults, item.ViewAndReduceKeyHashed, MakeKeyGrbit.NewKey);
				Api.JetSetIndexRange(session, MappedResults, SetIndexRangeGrbit.RangeUpperLimit | SetIndexRangeGrbit.RangeInclusive);
				do
				{
					int actualBookmarkSize;
					int ignored;
					Api.JetGetSecondaryIndexBookmark(session, MappedResults, ignoredBuffer, ignoredBuffer.Length, out ignored, bookmarkBuffer,
					                                 bookmarkBuffer.Length, out actualBookmarkSize, GetSecondaryIndexBookmarkGrbit.None);

					primaryKeyIndexes.Add(Tuple.Create(bookmarkBuffer.Take(actualBookmarkSize).ToArray(), item));
				} while (Api.TryMoveNext(session, MappedResults));
			}

			primaryKeyIndexes.Sort((x, y) =>
			{
				var bytes1 = x.Item1;
				var bytes2 = y.Item1;
				for (int i = 0; i < Math.Min(bytes1.Length, bytes2.Length); i++)
				{
					if (bytes1[i] != bytes2[i])
						return bytes1[i] - bytes2[i];
				}
				return bytes1.Length - bytes2.Length;
			});

			foreach (var primaryKeyIndexTuple in primaryKeyIndexes)
			{
				var bookmark = primaryKeyIndexTuple.Item1;
				var item = primaryKeyIndexTuple.Item2;
				Api.JetGotoBookmark(session, MappedResults, bookmark, bookmark.Length);

				// we need to check that we don't have hash collisions
				var currentReduceKey = Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["reduce_key"]);
				if (currentReduceKey != item.ReduceKey)
					continue;

				var currentView = Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["view"]);
				if (currentView != item.View)
					continue;

				yield return Api.RetrieveColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["data"]).ToJObject();
		
			}
		}

		public IEnumerable<string> DeleteMappedResultsForDocumentId(string documentId, string view)
		{
			Api.JetSetCurrentIndex(session, MappedResults, "by_view_and_doc_key");
			Api.MakeKey(session, MappedResults, view, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.MakeKey(session, MappedResults, documentId, Encoding.Unicode, MakeKeyGrbit.None);
			if (Api.TrySeek(session, MappedResults, SeekGrbit.SeekEQ) == false)
				return new string[0];

			var reduceKeys = new HashSet<string>();
			Api.MakeKey(session, MappedResults, view, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.MakeKey(session, MappedResults, documentId, Encoding.Unicode, MakeKeyGrbit.None);
			Api.JetSetIndexRange(session, MappedResults, SetIndexRangeGrbit.RangeUpperLimit | SetIndexRangeGrbit.RangeInclusive);
			do
			{
				// esent index ranges are approximate, and we need to check them ourselves as well
				var viewFromDb = Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["view"]);
				if (StringComparer.InvariantCultureIgnoreCase.Equals(viewFromDb, view) == false)
					continue;
				var documentIdFromDb = Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["document_key"]);
				if (StringComparer.InvariantCultureIgnoreCase.Equals(documentIdFromDb, documentId) == false)
					continue; 
				var reduceKey = Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["reduce_key"],
														   Encoding.Unicode);
				reduceKeys.Add(reduceKey);
				Api.JetDelete(session, MappedResults);
			} while (Api.TryMoveNext(session, MappedResults));
			return reduceKeys;
		}

		public void DeleteMappedResultsForView(string view)
		{
			Api.JetSetCurrentIndex(session, MappedResults, "by_view");
			Api.MakeKey(session, MappedResults, view, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, MappedResults, SeekGrbit.SeekEQ) == false)
				return;
			Api.MakeKey(session, MappedResults, view, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.JetSetIndexRange(session, MappedResults, SetIndexRangeGrbit.RangeUpperLimit | SetIndexRangeGrbit.RangeInclusive);

			do
			{
				// esent index ranges are approximate, and we need to check them ourselves as well
				var viewFromDb = Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["view"]);
				if (StringComparer.InvariantCultureIgnoreCase.Equals(viewFromDb, view) == false)
					continue;
				Api.JetDelete(session, MappedResults);
			} while (Api.TryMoveNext(session, MappedResults));
		}

	    public IEnumerable<MappedResultInfo> GetMappedResultsReduceKeysAfter(string indexName, Guid lastReducedEtag, bool loadData)
	    {
			Api.JetSetCurrentIndex(session, MappedResults, "by_view_and_etag");
			Api.MakeKey(session, MappedResults, indexName, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.MakeKey(session, MappedResults, lastReducedEtag, MakeKeyGrbit.None);
			if (Api.TrySeek(session, MappedResults, SeekGrbit.SeekLE) == false)
				yield break;

	        while (Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["view"]) == indexName)
	        {
	        	var result = new MappedResultInfo
	        	{
	        		ReduceKey = Api.RetrieveColumnAsString(session, MappedResults, tableColumnsCache.MappedResultsColumns["reduce_key"]),
	        		Etag = new Guid(Api.RetrieveColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["etag"])),
	        		Timestamp = Api.RetrieveColumnAsDateTime(session, MappedResults, tableColumnsCache.MappedResultsColumns["timestamp"]).Value,
	        	};
				if (loadData)
					result.Data = Api.RetrieveColumn(session, MappedResults, tableColumnsCache.MappedResultsColumns["data"]).ToJObject();
	        	yield return result;

				// the index is view ascending and etag descending
				// that means that we are going backward to go up
				if (Api.TryMovePrevious(session, MappedResults) == false)
					yield break;
	        }
	    }
	}
}
