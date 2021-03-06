using AutoTask.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PanoramicData.ConnectMagic.Service.Exceptions;
using PanoramicData.ConnectMagic.Service.Interfaces;
using PanoramicData.ConnectMagic.Service.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PanoramicData.ConnectMagic.Service.ConnectedSystemManagers
{
	internal class AutoTaskConnectedSystemManager : ConnectedSystemManagerBase
	{
		private readonly Client _autoTaskClient;
		private readonly ICache<JObject> _cache;

		public AutoTaskConnectedSystemManager(
			ConnectedSystem connectedSystem,
			State state,
			TimeSpan maxFileAge,
			ILoggerFactory loggerFactory)
			: base(connectedSystem, state, maxFileAge, loggerFactory.CreateLogger<AutoTaskConnectedSystemManager>())
		{
			// Ensure we have what we need
			if (string.IsNullOrWhiteSpace(connectedSystem?.Credentials?.PublicText))
			{
				throw new ConfigurationException($"ConnectedSystem '{connectedSystem!.Name}'s {nameof(connectedSystem.Credentials)} {nameof(connectedSystem.Credentials.PublicText)} must be set");
			}
			if (string.IsNullOrWhiteSpace(connectedSystem?.Credentials?.PrivateText))
			{
				throw new ConfigurationException($"ConnectedSystem '{connectedSystem!.Name}'s {nameof(connectedSystem.Credentials)} {nameof(connectedSystem.Credentials.PrivateText)} must be set");
			}
			if (string.IsNullOrWhiteSpace(connectedSystem?.Credentials?.ClientSecret))
			{
				throw new ConfigurationException($"ConnectedSystem '{connectedSystem!.Name}'s {nameof(connectedSystem.Credentials)} {nameof(connectedSystem.Credentials.ClientSecret)} must be set to the Integration Code");
			}

			// If the user provides an account, it's to set the server Id
			// This avoids the buggy AutoTask getZoneInfo call and improves performance
			ClientOptions? clientOptions = string.IsNullOrWhiteSpace(connectedSystem.Credentials.Account)
				? null
				: new ClientOptions
				{
					ServerId = int.TryParse(connectedSystem.Credentials.Account, out var serverId)
						? serverId
						: throw new ConfigurationException("Incorrectly-configured AutoTask Account Server Id")
				};

			_autoTaskClient = new Client(
				connectedSystem.Credentials.PublicText,
				connectedSystem.Credentials.PrivateText,
				connectedSystem.Credentials.ClientSecret,
				loggerFactory.CreateLogger<Client>(),
				clientOptions
			);
			_cache = new QueryCache<JObject>(TimeSpan.FromMinutes(1));
		}

		public override System.Threading.Tasks.Task ClearCacheAsync()
		{
			_cache.Clear();
			return System.Threading.Tasks.Task.CompletedTask;
		}

		public override async System.Threading.Tasks.Task RefreshDataSetAsync(ConnectedSystemDataSet dataSet, CancellationToken cancellationToken)
		{
			Logger.LogDebug($"Refreshing DataSet {dataSet.Name}");

			var inputText = dataSet.QueryConfig.Query ?? throw new ConfigurationException($"Missing Query in QueryConfig for dataSet '{dataSet.Name}'");
			var query = new SubstitutionString(inputText);
			var substitutedQuery = query.ToString();
			// Send the query off to AutoTask
			var autoTaskResult = await _autoTaskClient
				.GetAllAsync(substitutedQuery, cancellationToken)
				.ConfigureAwait(false);
			Logger.LogDebug($"Got {autoTaskResult.Count():N0} results for {dataSet.Name}.");
			// Convert to JObjects for easier generic manipulation
			var connectedSystemItems = autoTaskResult
				.Select(entity => JObject.FromObject(entity))
				.ToList();

			await ProcessConnectedSystemItemsAsync(
				dataSet,
				connectedSystemItems,
				ConnectedSystem,
				cancellationToken
				).ConfigureAwait(false);
		}

		/// <inheritdoc />
		internal override async Task<JObject> CreateOutwardsAsync(
			ConnectedSystemDataSet dataSet,
			JObject connectedSystemItem,
			CancellationToken cancellationToken
			)
		{
			// TODO - Handle functions
			var itemToCreate = MakeAutoTaskObject(dataSet, connectedSystemItem);
			return JObject.FromObject(await _autoTaskClient
				.CreateAsync(itemToCreate, cancellationToken)
				.ConfigureAwait(false));
		}

		private static Entity MakeAutoTaskObject(ConnectedSystemDataSet dataSet, JObject connectedSystemItem)
		{
			var type = Type.GetType($"AutoTask.Api.{dataSet.QueryConfig.Type}, {typeof(Entity).Assembly.FullName}")
				?? throw new ConfigurationException($"AutoTask type {dataSet.QueryConfig.Type} not supported.");

			var instance = Activator.CreateInstance(type)
				?? throw new ConfigurationException($"AutoTask type {dataSet.QueryConfig.Type} could not be created.");

			var connectedSystemItemPropertyNames = connectedSystemItem.Properties().Select(p => p.Name);

			var typePropertyInfos = type.GetProperties();
			foreach (var propertyInfo in typePropertyInfos.Where(pi => connectedSystemItemPropertyNames.Contains(pi.Name)))
			{
				propertyInfo.SetValue(instance, connectedSystemItem[propertyInfo.Name]!.ToObject(propertyInfo.PropertyType));
			}

			var entity = (Entity)instance;

			const string UserDefinedFieldPrefix = "UserDefinedFields.";

			// Set the UserDefinedFields
			var udfNamesToSet = connectedSystemItemPropertyNames
				.Where(n => n.StartsWith(UserDefinedFieldPrefix))
				.ToList();

			// Do we have UDFs to update?
			if (udfNamesToSet.Count > 0)
			{
				// Yes

				// It is possible that the entity does not have a UserDefinedFields property set if it is either:
				// - being created from scratch
				// - or had all UDFs set to null
				// Is this the case?
				if (entity.UserDefinedFields == null)
				{
					// Yes - they are not present.  Create a new array.
					entity.UserDefinedFields = udfNamesToSet.Select(udfName => new UserDefinedField
					{
						Name = udfName[UserDefinedFieldPrefix.Length..],
						Value = connectedSystemItem[udfName]!.ToString()
					}
					).ToArray();
				}
				else
				{
					// No - they ARE present - just update them.
					foreach (var connectedSystemItemUdfName in udfNamesToSet)
					{
						var targetFieldName = connectedSystemItemUdfName[UserDefinedFieldPrefix.Length..];

						var targetField = entity.UserDefinedFields.SingleOrDefault(udf => udf.Name == targetFieldName)
							?? throw new ConfigurationException($"Could not find UserDefinedField {targetFieldName} on Entity.");

						targetField.Value = connectedSystemItem[connectedSystemItemUdfName]!.ToString();
					}
				}
			}

			return entity;
		}

		/// <inheritdoc />
		internal override async System.Threading.Tasks.Task DeleteOutwardsAsync(
			ConnectedSystemDataSet dataSet,
			JObject connectedSystemItem,
			CancellationToken cancellationToken
			)
		{
			var entity = MakeAutoTaskObject(dataSet, connectedSystemItem);
			Logger.LogDebug($"Deleting item with id {entity.id}");
			await _autoTaskClient
				.DeleteAsync(entity, cancellationToken)
				.ConfigureAwait(false);
		}

		/// <inheritdoc />
		internal async override System.Threading.Tasks.Task UpdateOutwardsAsync(
			ConnectedSystemDataSet dataSet,
			SyncAction syncAction,
			CancellationToken cancellationToken
			)
		{
			if (syncAction.ConnectedSystemItem == null)
			{
				throw new InvalidOperationException($"{nameof(syncAction.ConnectedSystemItem)} must not be null when Updating Outwards.");
			}

			if (syncAction.Functions.Count != 0)
			{
				throw new NotSupportedException("Implement functions");
			}

			// Handle simple update
			var existingItem = MakeAutoTaskObject(dataSet, syncAction.ConnectedSystemItem);
			var _ = await _autoTaskClient
				.UpdateAsync(existingItem, cancellationToken)
				.ConfigureAwait(false);
		}

		public override async Task<object?> QueryLookupAsync(
			QueryConfig queryConfig,
			string field,
			bool valueIfZeroMatchesFoundSets,
			object? valueIfZeroMatchesFound,
			bool valueIfMultipleMatchesFoundSets,
			object? valueIfMultipleMatchesFound,
			CancellationToken cancellationToken)
		{
			try
			{
				var cacheKey = queryConfig.Query ?? throw new ConfigurationException("Query must be provided when performing lookups.");

				Logger.LogTrace($"Performing lookup: for field {field}\n{queryConfig.Query}");

				// Is it cached?
				JObject connectedSystemItem;
				if (_cache.TryGet(cacheKey, out var @object))
				{
					// Yes. Use that
					connectedSystemItem = @object!;
				}
				else
				{
					// No.

					var autoTaskResult = (await _autoTaskClient
								.QueryAsync(queryConfig.Query, cancellationToken)
								.ConfigureAwait(false))
								.ToList();

					switch (autoTaskResult.Count)
					{
						case 0:
							if (valueIfZeroMatchesFoundSets)
							{
								return valueIfZeroMatchesFound;
							}
							throw new LookupException($"Got 0 results for QueryLookup '{queryConfig.Query}' and no default value is configured.");
						case 1:
							// Convert to JObjects for easier generic manipulation
							connectedSystemItem = autoTaskResult
								.Select(entity => JObject.FromObject(entity))
								.Single();

							_cache.Store(cacheKey, connectedSystemItem);
							break;
						default:
							if (valueIfMultipleMatchesFoundSets)
							{
								return valueIfMultipleMatchesFound;
							}
							throw new LookupException($"Got {autoTaskResult.Count} results for QueryLookup '{queryConfig.Query}' and no default value is configured.");
					}
				}

				// Determine the field value
				if (!connectedSystemItem.TryGetValue(field, out var fieldValue))
				{
					throw new ConfigurationException($"Field {field} not present for QueryLookup.");
				}
				return fieldValue;
			}
			catch (ConfigurationException exception)
			{
				Logger.LogWarning(exception, "Failed to Lookup due to config");
				throw;
			}
			catch (LookupException exception)
			{
				Logger.LogWarning(exception, "Failed to Lookup due to response count");
				throw;
			}
			catch (Exception exception)
			{
				Logger.LogError(exception, "Unexpected exception in QueryLookupAsync");
				throw;
			}
		}

		public override System.Threading.Tasks.Task PatchAsync(
			string entityClass,
			string entityId,
			Dictionary<string, object> patches,
			CancellationToken cancellationToken
			)
			=> throw new NotSupportedException();

		public override void Dispose()
			=> _autoTaskClient?.Dispose();
	}
}