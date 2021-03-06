﻿using Raven.Abstractions.Data;
using Raven.Studio.Framework;

namespace Raven.Studio.Features.Statistics
{
	using System.Collections.Generic;
	using System.ComponentModel.Composition;
	using System.Linq;
	using Caliburn.Micro;
	using Plugins;

	//NOTE: it would probably make more sense to remove IServer.Errors and rely on the message StatisticsUpdated
	[Export]
	public class ErrorsViewModel : RavenScreen
	{

		[ImportingConstructor]
		public ErrorsViewModel(IServer server)
		{
			DisplayName = "Errors";
			Server = server;
			server.CurrentDatabaseChanged += delegate { NotifyOfPropertyChange( ()=> Errors );};
		}

		public IEnumerable<Error> Errors
		{
			get { return Server.Errors.Select( x => new Error(x)); }
		}

	}

	public class Error
	{
		private readonly ServerError inner;

		public Error(ServerError inner)
		{
			this.inner = inner;
		}

		public ServerError Inner { get { return inner; } }
	}
}