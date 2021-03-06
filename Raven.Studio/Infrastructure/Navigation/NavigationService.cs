﻿// -----------------------------------------------------------------------
//  <copyright file="CanGetStats.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Browser;
using Raven.Studio.Features.Database;
using Raven.Studio.Plugins;

namespace Raven.Studio.Infrastructure.Navigation
{
	[Export]
	public class NavigationService
	{
		private readonly IServer server;

		[ImportingConstructor]
		public NavigationService(IServer server)
		{
			this.server = server;
		}

		private string currentUrl;
		private Regex databasesRegEx = new Regex("^databases/([^/]+)");

		[ImportMany(AllowRecomposition = true)]
		public Lazy<INavigator, INavigatorMetdata>[] Routes { get; set; }

		public void Initialize()
		{
			Application.Current.Host.NavigationStateChanged += (sender, args) => Navigate(args.NewNavigationState);
			if (string.IsNullOrEmpty(Application.Current.Host.NavigationState))
				return;

			Navigate(Application.Current.Host.NavigationState);
		}

		private void Navigate(string navigationState)
		{
			if (navigationState == currentUrl)
				return;
			currentUrl = navigationState;

			string database = Server.DefaultDatabaseName;
			var databasesMatch = databasesRegEx.Match(currentUrl);
			if (databasesMatch.Success)
			{
				currentUrl = currentUrl.Substring(databasesMatch.Length);
				database = databasesMatch.Groups[1].Value;
			}

			foreach (var route in Routes.OrderBy(x => x.Metadata.Index))
			{
				var regex = new Regex(route.Metadata.Url);
				var match = regex.Match(navigationState);
				if (match.Success == false)
					continue;

				var parameters = new Dictionary<string, string>();
				foreach (var name in regex.GetGroupNames())
				{
					parameters[name] = match.Groups[name].Value;
				}
				route.Value.Navigate(database, parameters);
				return;
			}
		}

		public void Track(NavigationState navigationState)
		{
			if (navigationState.Url == currentUrl)
				return;
			currentUrl = navigationState.Url;

			if (server.CurrentDatabase != Server.DefaultDatabaseName)
				navigationState.Url = "databases/" + server.CurrentDatabase + "/" + navigationState.Url;

			// Application.Current.IsRunningOutOfBrowser
			Application.Current.Host.NavigationState = navigationState.Url;
			HtmlPage.Document.SetProperty("title", navigationState.Title);
		}
	}
}