using LogicMonitor.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PanoramicData.ConnectMagic.Service.Exceptions;
using PanoramicData.ConnectMagic.Service.Interfaces;
using PanoramicData.ConnectMagic.Service.Models;
using PanoramicData.NCalcExtensions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PanoramicData.ConnectMagic.Service.ConnectedSystemManagers
{
	internal class LogicMonitorConnectedSystemManager : ConnectedSystemManagerBase
	{
		private readonly PortalClient _logicMonitorClient;
		private readonly ILogger _logger;
		private readonly ICache<JObject> _cache;

		public LogicMonitorConnectedSystemManager(
			ConnectedSystem connectedSystem,
			State state,
			TimeSpan maxFileAge,
			ILogger<LogicMonitorConnectedSystemManager> logger)
			: base(connectedSystem, state, maxFileAge, logger)
		{
			_logicMonitorClient = new PortalClient(connectedSystem.Credentials.Account, connectedSystem.Credentials.PublicText, connectedSystem.Credentials.PrivateText, logger);
			_logger = logger;
			_cache = new QueryCache<JObject>(TimeSpan.FromMinutes(1));
		}

		public override async Task RefreshDataSetAsync(ConnectedSystemDataSet dataSet, CancellationToken cancellationToken)
		{
			_logger.LogDebug($"Refreshing DataSet {dataSet.Name}");
			var inputText = dataSet.QueryConfig.Query ?? throw new ConfigurationException($"Missing Query in QueryConfig for dataSet '{dataSet.Name}'");
			var query = new SubstitutionString(inputText);
			// Send the query off to LogicMonitor
			var connectedSystemItems = await _logicMonitorClient
				.GetAllAsync<JObject>(query.ToString(), cancellationToken)
				.ConfigureAwait(false);
			_logger.LogDebug($"Got {connectedSystemItems.Count} results for {dataSet.Name}.");

			await ProcessConnectedSystemItemsAsync(
				dataSet,
				connectedSystemItems,
				GetFileInfo(ConnectedSystem, dataSet),
				cancellationToken
				).ConfigureAwait(false);
		}

		/// <inheritdoc />
		internal override async Task CreateOutwardsAsync(
			ConnectedSystemDataSet dataSet,
			JObject connectedSystemItem,
			CancellationToken cancellationToken
			)
		{
			var endpoint = new SubstitutionString(dataSet.QueryConfig.CreateQuery ?? dataSet.QueryConfig.Query).ToString();
			var _ = await _logicMonitorClient.PostAsync<JObject, JObject>(
				connectedSystemItem,
				endpoint,
				cancellationToken).ConfigureAwait(false);
		}

		/// <inheritdoc />
		internal override async Task DeleteOutwardsAsync(
			ConnectedSystemDataSet dataSet,
			JObject connectedSystemItem,
			CancellationToken cancellationToken)
		{
			var endpoint = new SubstitutionString(dataSet.QueryConfig.DeleteQuery ?? dataSet.QueryConfig.Query).ToString();
			await _logicMonitorClient.DeleteAsync(endpoint, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Strategy: create a Patch containing all of the fields in the connectedSystemItem
		/// </summary>
		/// <param name="dataSet"></param>
		/// <param name="connectedSystemItem"></param>
		/// <returns></returns>
		/// <exception cref="NotSupportedException"></exception>
		internal override Task UpdateOutwardsAsync(
			ConnectedSystemDataSet dataSet,
			JObject connectedSystemItem,
			CancellationToken cancellationToken)
		{
			var endpoint = new SubstitutionString(dataSet.QueryConfig.UpdateQuery ?? dataSet.QueryConfig.Query).ToString();
			return _logicMonitorClient.PutAsync(endpoint, connectedSystemItem, cancellationToken);
		}

		public override async Task<object> QueryLookupAsync(QueryConfig queryConfig, string field, CancellationToken cancellationToken)
		{
			try
			{
				var cacheKey = queryConfig.Query;
				_logger.LogDebug($"Performing lookup: for field {field}\n{queryConfig.Query}");

				// Is it cached?
				JObject connectedSystemItem;
				if (_cache.TryGet(cacheKey, out var @object))
				{
					// Yes. Use that
					connectedSystemItem = @object;
				}
				else
				{
					// No.
					switch (queryConfig.Type)
					{
						case "Single":
							connectedSystemItem = await _logicMonitorClient
								.GetJObjectAsync(queryConfig.Query, cancellationToken)
								.ConfigureAwait(false);
							break;
						default:
							var connectedSystemItems = await _logicMonitorClient
								.GetAllAsync<JObject>(queryConfig.Query, cancellationToken)
								.ConfigureAwait(false);

							if (connectedSystemItems.Count != 1)
							{
								throw new LookupException($"Got {connectedSystemItems.Count} results for QueryLookup '{queryConfig.Query}'. Expected one.");
							}

							// Convert to JObjects for easier generic manipulation
							connectedSystemItem = connectedSystemItems[0];
							break;
					}

					_cache.Store(cacheKey, connectedSystemItem);
				}

				var expression = new ExtendedExpression(field);
				expression.Parameters["result"] = connectedSystemItem;
				try
				{
					return expression.Evaluate();
				}
				catch (Exception e)
				{
					throw new ConfigurationException($"Field {field} not present for QueryLookup: {e.Message}.");
				}
			}
			catch (Exception e)
			{
				_logger.LogError(e, "Failed to Lookup");
				throw;
			}
		}

		public override Task ClearCacheAsync()
		{
			_cache.Clear();
			return Task.CompletedTask;
		}

		public override void Dispose()
			=> _logicMonitorClient?.Dispose();
	}
}