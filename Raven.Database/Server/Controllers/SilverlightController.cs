﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Web.Http;
using Raven.Abstractions.MEF;
using Raven.Database.Plugins;

namespace Raven.Database.Server.Controllers
{
	[RoutePrefix("")]
	public class SilverlightController : RavenApiController
	{
		[ImportMany]
		public OrderedPartCollection<ISilverlightRequestedAware> SilverlightRequestedAware { get; set; }

		[HttpGet("silverlight/ensureStartup")]
		public HttpResponseMessage SilverlightEnsureStartup()
		{
			Database.ExtensionsState.GetOrAdd("SilverlightUI.NotifiedAboutSilverlightBeingRequested", s =>
			{
				var skipCreatingStudioIndexes = Database.Configuration.Settings["Raven/SkipCreatingStudioIndexes"];
				if (string.IsNullOrEmpty(skipCreatingStudioIndexes) == false &&
					"true".Equals(skipCreatingStudioIndexes, StringComparison.OrdinalIgnoreCase))
					return true;

				foreach (var silverlightRequestedAware in SilverlightRequestedAware)
				{
					silverlightRequestedAware.Value.SilverlightWasRequested(Database);
				}
				return true;
			});

			return GetMessageWithObject(new { ok = true });
		}

		//TODO: fix the routing 
		[HttpGet("silverlight/{*id}")]
		public HttpResponseMessage SilverlightUi(string id)
		{
			Database.ExtensionsState.GetOrAdd("SilverlightUI.NotifiedAboutSilverlightBeingRequested", s =>
			{
				foreach (var silverlightRequestedAware in SilverlightRequestedAware)
				{
					silverlightRequestedAware.Value.SilverlightWasRequested(Database);
				}
				return true;
			});

			var fileName = id;
			var paths = GetPaths(fileName, Database.Configuration.WebDir);
			
			var matchingPath = paths.FirstOrDefault(path =>
			{
				try
				{
					return File.Exists(path);
				}
				catch (Exception)
				{
					return false;
				}
			});
			
			if (matchingPath != null)
			{
				return WriteFile(matchingPath);
			}

			return WriteEmbeddedFile(DatabasesLandlord.SystemConfiguration.WebDir, "Raven.Studio.xap");
		}

		public static IEnumerable<string> GetPaths(string fileName, string webDir)
		{
			yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"sl5", fileName);
			// dev path
			yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\Raven.Studio\bin\debug", fileName);
			// dev path
			yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\Raven.Studio\bin\debug", fileName);
			//local path
			yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
			//local path, bin folder
			yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", fileName);

			// web ui path
			yield return Path.Combine(webDir, fileName);

			var options = new[]
			              	{
			              		@"..\..\..\packages", // assuming we are in slnDir\Project.Name\bin\debug 		
			              		@"..\..\packages"
			              	};
			foreach (var option in options)
			{
				try
				{
					if (Directory.Exists(option) == false)
						continue;
				}
				catch (Exception)
				{
					yield break;
				}
				string[] directories;
				try
				{
					directories = Directory.GetDirectories(option, "RavenDB.Embedded*");
				}
				catch (Exception)
				{
					yield break;
				}
				foreach (var dir in directories.OrderByDescending(x => x))
				{
					var contentDir = Path.Combine(dir, "content");
					bool exists;
					try
					{
						exists = Directory.Exists(contentDir);
					}
					catch (Exception)
					{
						continue;
					}
					if (exists)
						yield return contentDir;
				}
			}
		}
	}
}
