﻿using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PanoramicData.ConnectMagic.Service.Config;
using PanoramicData.ConnectMagic.Service.ConnectedSystemManagers;
using PanoramicData.ConnectMagic.Service.Interfaces;
using PanoramicData.ConnectMagic.Service.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PanoramicData.ConnectMagic.Service
{
	/// <summary>
	/// A ConnectMagic Service
	/// </summary>
	internal partial class ConnectMagicService : ServiceBase, IHostedService
	{
		private const string EventLogSourceName = Program.ProductName;
		private readonly EventLogClient? _eventLogClient;
		private readonly ILogger _logger;
		private readonly Configuration _configuration;
		private readonly CancellationTokenSource _cancellationTokenSource;
		private State _state;
		private Task? _startLoopsTask;
		private readonly TimeSpan _maxFileAge;
		private readonly List<Task> _connectedSystemTasks;
		private readonly FileInfo? _stateFileInfo;
		private readonly ILoggerFactory _loggerFactory;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="options">The options</param>
		public ConnectMagicService(
			ILoggerFactory loggerFactory,
			IOptions<Configuration> options
			)
		{
			InitializeComponent();

			_logger = loggerFactory.CreateLogger<ConnectMagicService>();
			_configuration = options.Value;
			_cancellationTokenSource = new CancellationTokenSource();

			// Create State object
			_state = _configuration.State;

			// Store Max file age
			_maxFileAge = TimeSpan.FromHours(_configuration.MaxFileAgeHours);

			// Create task list
			_connectedSystemTasks = new List<Task>();

			// Set up the Event Log
			try
			{
				if (!EventLog.SourceExists(EventLogSourceName))
				{
					EventLog.CreateEventSource(EventLogSourceName, "Application");
				}
				if (EventLog.SourceExists(EventLogSourceName))
				{
					_eventLogClient = new EventLogClient(EventLogSourceName);
				}
			}
			catch
			{
				// This is OK, we just don't have access to the event log
			}

			if (!string.IsNullOrWhiteSpace(_configuration.State.CacheFileName))
			{
				_stateFileInfo = new FileInfo(_configuration.State.CacheFileName);
			}
			_loggerFactory = loggerFactory;
		}

		/// <summary>
		/// Tasks to perform on start
		/// </summary>
		protected override void OnStart(string[] args)
		{
			_eventLogClient?.WriteToEventLog($"Starting Service Version {ThisAssembly.AssemblyFileVersion}", EventLogEntryType.Information, (int)EventId.Starting);
			StartAsync(default)
				.ConfigureAwait(false)
				.GetAwaiter()
				.GetResult();
			_eventLogClient?.WriteToEventLog("Service Started", EventLogEntryType.Information, (int)EventId.Started);
		}

		/// <summary>
		/// Tasks to perform on stop
		/// </summary>
		protected override void OnStop()
		{
			_eventLogClient?.WriteToEventLog("Service Stopping", EventLogEntryType.Information, (int)EventId.Stopping);
			StopAsync(default)
				.ConfigureAwait(false)
				.GetAwaiter()
				.GetResult();
			_eventLogClient?.WriteToEventLog("Service Stopped", EventLogEntryType.Information, (int)EventId.Stopped);
		}

		/// <summary>
		/// Tasks to perform on shutdown
		/// </summary>
		protected override void OnShutdown()
		{
			_eventLogClient?.WriteToEventLog("Service Stopping due to shutdown", EventLogEntryType.Information, (int)EventId.Stopping);
			StopAsync(default)
				.ConfigureAwait(false)
				.GetAwaiter()
				.GetResult();
			_eventLogClient?.WriteToEventLog("Service Stopped due to shutdown", EventLogEntryType.Information, (int)EventId.Stopped);
		}

		/// <summary>
		/// Starts work
		/// </summary>
		/// <param name="cancellationToken"></param>
		public Task StartAsync(CancellationToken cancellationToken)
		{
			_logger.LogInformation($"Starting {Program.ProductName} {ThisAssembly.AssemblyFileVersion} on {Environment.MachineName}...");
			_logger.LogInformation($"Loaded configuration \"{_configuration.Name}\":\"{_configuration.Description}\"");

			// Add an unhandled exception handler
			var currentDomain = AppDomain.CurrentDomain;
			currentDomain.UnhandledException += CurrentDomain_UnhandledException;

			_configuration.Validate();

			_state = _configuration.State;

			if (_stateFileInfo != null)
			{
				// Load State from a file if it exists
				try
				{
					var loadedState = State.FromFile(_stateFileInfo);
					_state.ItemLists = loadedState.ItemLists;
				}
				catch (Exception e)
				{
					_logger.LogError(e, $"Could not load state from file: '{e.Message}'");
				}
			}

			_state.ConnectedSystemManagers = _configuration
				.ConnectedSystems
				.Where(cs => cs.IsEnabled)
				.Select(cs => CreateConnectedSystemManager(cs, _state, _maxFileAge))
				.ToDictionary(csm => csm.ConnectedSystem.Name);

			// Fire and forget
			_startLoopsTask = StartLoopsAsync(cancellationToken);

			_logger.LogDebug($"Started {Program.ProductName}.");

			return Task.CompletedTask;
		}

		private async Task StartLoopsAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			_logger.LogInformation("Initial sync starting");

			// Execute them all at least once.
			while (!_state.IsConnectedSystemsSyncCompletedOnce())
			{
				// Create ConnectedSystemPeriodLoops for Connected System Managers that are not fully synced with State.
				var connectedSystemPeriodStartupLoops = _state
					.ConnectedSystemManagers
					.Where(csm => csm.Value.Stats.LastSyncCompleted == DateTimeOffset.MinValue)
					.Select(connectedSystemManagerKvp => new ConnectedSystemPeriodLoop(connectedSystemManagerKvp.Value, _loggerFactory.CreateLogger<ConnectedSystemPeriodLoop>()))
					.ToList();

				try
				{
					foreach (var connectedSystemPeriodLoop in connectedSystemPeriodStartupLoops)
					{
						await connectedSystemPeriodLoop
							.ExecuteAsync(_cancellationTokenSource.Token)
							.ConfigureAwait(false);
					}

					_logger.LogInformation("Initial sync complete");
					// The syncs have all executed at least once.
				}
				catch (Exception e)
				{
					const int delaySeconds = 10;
					_logger.LogError(e, $"Initial sync failed.  Retrying in {delaySeconds}s...");
					await Task
						.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken)
						.ConfigureAwait(false);
				}
			}
			_logger.LogDebug("**********************************************************************************");
			// We completed an initial sync

			var connectedSystemPeriodLoops = _state
				.ConnectedSystemManagers
				.Select(connectedSystemManagerKvp => new ConnectedSystemPeriodLoop(connectedSystemManagerKvp.Value, _loggerFactory.CreateLogger<ConnectedSystemPeriodLoop>()))
				.ToList();

			// Immediately start looping, with the configured delays from now on.
			foreach (var connectedSystemPeriodLoop in connectedSystemPeriodLoops)
			{
				// TODO - DA: What to do if one of the connected systems faults? Restart all, or continue to attempt to restart that system?
				_connectedSystemTasks.Add(
					connectedSystemPeriodLoop.LoopAsync(_cancellationTokenSource.Token)
					.ContinueWith(faultingTask =>
					{
						var sb = new StringBuilder();
						if (faultingTask.Exception != null)
						{
							foreach (var e in faultingTask.Exception.Flatten().InnerExceptions)
							{
								sb.AppendLine(e.ToString());
							}
						}
						else
						{
							sb.AppendLine("The exception was not set");
						}
						_logger.LogError($"Exception in system task for connected system {connectedSystemPeriodLoop.ConnectedSystemName}: {sb}");
					}, TaskContinuationOptions.OnlyOnFaulted)
				);
			}
		}

		private IConnectedSystemManager CreateConnectedSystemManager(ConnectedSystem connectedSystem, State state, TimeSpan maxFileAge)
			=> connectedSystem.Type switch
			{
				SystemType.AutoTask => new AutoTaskConnectedSystemManager(connectedSystem, state, maxFileAge, _loggerFactory),
				SystemType.Certify => new CertifyConnectedSystemManager(connectedSystem, state, maxFileAge, _loggerFactory),
				SystemType.LogicMonitor => new LogicMonitorConnectedSystemManager(connectedSystem, state, maxFileAge, _loggerFactory),
				SystemType.SalesForce => new SalesforceConnectedSystemManager(connectedSystem, state, maxFileAge, _loggerFactory),
				SystemType.MsSqlServer => new MsSqlServerConnectedSystemManager(connectedSystem, state, maxFileAge, _loggerFactory),
				SystemType.ServiceNow => new ServiceNowConnectedSystemManager(connectedSystem, state, maxFileAge, _loggerFactory),
				_ => throw new NotSupportedException($"Unsupported ConnectedSystem type: '{connectedSystem.Type}'")
			};

		/// <summary>
		/// Tasks to perform on stop
		/// </summary>
		/// <param name="cancellationToken"></param>
		public async Task StopAsync(CancellationToken cancellationToken)
		{
			_logger.LogDebug($"Stopping {Program.ProductName}...");

			// Stop Remote System Tasks
			_cancellationTokenSource.Cancel();

			try
			{
				if (_startLoopsTask != null)
				{
					_logger.LogDebug("Ensuring StartLoopsTask is complete");
					await _startLoopsTask.ConfigureAwait(false);
				}

				if (_connectedSystemTasks != null)
				{
					_logger.LogDebug("Waiting for ConnectedSystemTasks to complete...");
					await Task.WhenAll(_connectedSystemTasks.ToArray()).ConfigureAwait(false);
				}
			}
			catch (Exception ex) when (ex is OperationCanceledException || ex is TaskCanceledException)
			{
				_logger.LogDebug("Cancelled");
			}

			try
			{
				if (_stateFileInfo != null)
				{
					// Save lastKnownState
					_state.Save(_stateFileInfo);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, ex.Message);
			}
			finally
			{
				_logger.LogInformation($"Stopped {Program.ProductName}.");
			}
		}

		/// <summary>
		///    Writes the nature of the problem to the Windows event log
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
			=> _eventLogClient?.WriteToEventLog(
				e?.ExceptionObject?.ToString() ?? string.Empty,
				EventLogEntryType.Error,
				(int)EventId.UnhandledException);
	}
}
