using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
#if SMAPI_FOR_WINDOWS
using System.Windows.Forms;
#endif
using Newtonsoft.Json;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Logging;
using StardewModdingAPI.Framework.Models;
using StardewModdingAPI.Framework.ModHelpers;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Framework.Patching;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Serialisation;
using StardewModdingAPI.Internal;
using StardewModdingAPI.Toolkit;
using StardewModdingAPI.Toolkit.Framework.Clients.WebApi;
using StardewModdingAPI.Toolkit.Framework.ModData;
using StardewModdingAPI.Toolkit.Serialisation;
using StardewModdingAPI.Toolkit.Utilities;
using StardewValley;
using Object = StardewValley.Object;
using ThreadState = System.Threading.ThreadState;

namespace StardewModdingAPI.Framework
{
    /// <summary>The core class which initialises and manages SMAPI.</summary>
    internal class SCore : IDisposable
    {
        /*********
        ** Properties
        *********/
        /// <summary>The log file to which to write messages.</summary>
        private readonly LogFileManager LogFile;

        /// <summary>Manages console output interception.</summary>
        private readonly ConsoleInterceptionManager ConsoleManager = new ConsoleInterceptionManager();

        /// <summary>The core logger and monitor for SMAPI.</summary>
        private readonly Monitor Monitor;

        /// <summary>Tracks whether the game should exit immediately and any pending initialisation should be cancelled.</summary>
        private readonly CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        /// <summary>Simplifies access to private game code.</summary>
        private readonly Reflector Reflection = new Reflector();

        /// <summary>The SMAPI configuration settings.</summary>
        private readonly SConfig Settings;

        /// <summary>The underlying game instance.</summary>
        private SGame GameInstance;

        /// <summary>The underlying content manager.</summary>
        private ContentCoordinator ContentCore => this.GameInstance.ContentCore;

        /// <summary>Tracks the installed mods.</summary>
        /// <remarks>This is initialised after the game starts.</remarks>
        private readonly ModRegistry ModRegistry = new ModRegistry();

        /// <summary>Manages deprecation warnings.</summary>
        /// <remarks>This is initialised after the game starts.</remarks>
        private DeprecationManager DeprecationManager;

        /// <summary>Manages SMAPI events for mods.</summary>
        private readonly EventManager EventManager;

        /// <summary>Whether the game is currently running.</summary>
        private bool IsGameRunning;

        /// <summary>Whether the program has been disposed.</summary>
        private bool IsDisposed;

        /// <summary>Regex patterns which match console messages to suppress from the console and log.</summary>
        private readonly Regex[] SuppressConsolePatterns =
        {
            new Regex(@"^TextBox\.Selected is now '(?:True|False)'\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^(?:FRUIT )?TREE: IsClient:(?:True|False) randomOutput: \d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^loadPreferences\(\); begin", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^savePreferences\(\); async=", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^DebugOutput: (?:added CLOUD|dismount tile|Ping|playerPos)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^static SerializableDictionary<.+>\(\) called\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        };

        /// <summary>The mod toolkit used for generic mod interactions.</summary>
        private readonly ModToolkit Toolkit = new ModToolkit();

        /// <summary>The path to search for mods.</summary>
        private readonly string ModsPath;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="modsPath">The path to search for mods.</param>
        /// <param name="writeToConsole">Whether to output log messages to the console.</param>
        public SCore(string modsPath, bool writeToConsole)
        {
            // init paths
            this.VerifyPath(modsPath);
            this.VerifyPath(Constants.LogDir);
            this.ModsPath = modsPath;

            // init log file
            this.PurgeLogFiles();
            string logPath = this.GetLogPath();

            // init basics
            this.Settings = JsonConvert.DeserializeObject<SConfig>(File.ReadAllText(Constants.ApiConfigPath));
            this.LogFile = new LogFileManager(logPath);
            this.Monitor = new Monitor("SMAPI", this.ConsoleManager, this.LogFile, this.CancellationTokenSource, this.Settings.ColorScheme)
            {
                WriteToConsole = writeToConsole,
                ShowTraceInConsole = this.Settings.DeveloperMode,
                ShowFullStampInConsole = this.Settings.DeveloperMode
            };
            this.EventManager = new EventManager(this.Monitor, this.ModRegistry);

            // init logging
            this.Monitor.Log($"SMAPI {Constants.ApiVersion} with Stardew Valley {Constants.GameVersion} on {EnvironmentUtility.GetFriendlyPlatformName(Constants.Platform)}", LogLevel.Info);
            this.Monitor.Log($"Mods go here: {modsPath}");
            if (modsPath != Constants.DefaultModsPath)
                this.Monitor.Log("(Using custom --mods-path argument.)", LogLevel.Trace);
            this.Monitor.Log($"Log started at {DateTime.UtcNow:s} UTC", LogLevel.Trace);

            // validate game version
            if (Constants.GameVersion.IsOlderThan(Constants.MinimumGameVersion))
            {
                this.Monitor.Log($"Oops! You're running Stardew Valley {Constants.GameVersion}, but the oldest supported version is {Constants.MinimumGameVersion}. Please update your game before using SMAPI.", LogLevel.Error);
                this.PressAnyKeyToExit();
                return;
            }
            if (Constants.MaximumGameVersion != null && Constants.GameVersion.IsNewerThan(Constants.MaximumGameVersion))
            {
                this.Monitor.Log($"Oops! You're running Stardew Valley {Constants.GameVersion}, but this version of SMAPI is only compatible up to Stardew Valley {Constants.MaximumGameVersion}. Please check for a newer version of SMAPI: https://smapi.io.", LogLevel.Error);
                this.PressAnyKeyToExit();
                return;
            }

            // apply game patches
            new GamePatcher(this.Monitor).Apply(
            //    new GameLocationPatch()
            );
        }

        /// <summary>Launch SMAPI.</summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical] // let try..catch handle corrupted state exceptions
        public void RunInteractively()
        {
            // initialise SMAPI
            try
            {
                // hook up events
                ContentEvents.Init(this.EventManager);
                ControlEvents.Init(this.EventManager);
                GameEvents.Init(this.EventManager);
                GraphicsEvents.Init(this.EventManager);
                InputEvents.Init(this.EventManager);
                LocationEvents.Init(this.EventManager);
                MenuEvents.Init(this.EventManager);
                MineEvents.Init(this.EventManager);
                MultiplayerEvents.Init(this.EventManager);
                PlayerEvents.Init(this.EventManager);
                SaveEvents.Init(this.EventManager);
                SpecialisedEvents.Init(this.EventManager);
                TimeEvents.Init(this.EventManager);

                // init JSON parser
                JsonConverter[] converters = {
                    new ColorConverter(),
                    new PointConverter(),
                    new RectangleConverter()
                };
                foreach (JsonConverter converter in converters)
                    this.Toolkit.JsonHelper.JsonSettings.Converters.Add(converter);

                // add error handlers
#if SMAPI_FOR_WINDOWS
                Application.ThreadException += (sender, e) => this.Monitor.Log($"Critical thread exception: {e.Exception.GetLogSummary()}", LogLevel.Error);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
#endif
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => this.Monitor.Log($"Critical app domain exception: {e.ExceptionObject}", LogLevel.Error);

                // add more leniant assembly resolvers
                AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => AssemblyLoader.ResolveAssembly(e.Name);

                // override game
                SGame.ConstructorHack = new SGameConstructorHack(this.Monitor, this.Reflection, this.Toolkit.JsonHelper);
                this.GameInstance = new SGame(this.Monitor, this.Reflection, this.EventManager, this.InitialiseAfterGameStart, this.Dispose);
                StardewValley.Program.gamePtr = this.GameInstance;

                // add exit handler
                new Thread(() =>
                {
                    this.CancellationTokenSource.Token.WaitHandle.WaitOne();
                    if (this.IsGameRunning)
                    {
                        try
                        {
                            File.WriteAllText(Constants.FatalCrashMarker, string.Empty);
                            File.Copy(this.LogFile.Path, Constants.FatalCrashLog, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"SMAPI failed trying to track the crash details: {ex.GetLogSummary()}");
                        }

                        this.GameInstance.Exit();
                    }
                }).Start();

                // hook into game events
                ContentEvents.AfterLocaleChanged += (sender, e) => this.OnLocaleChanged();

                // set window titles
                this.GameInstance.Window.Title = $"Stardew Valley {Constants.GameVersion} - running SMAPI {Constants.ApiVersion}";
                Console.Title = $"SMAPI {Constants.ApiVersion} - running Stardew Valley {Constants.GameVersion}";
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"SMAPI failed to initialise: {ex.GetLogSummary()}", LogLevel.Error);
                this.PressAnyKeyToExit();
                return;
            }

            // check update marker
            if (File.Exists(Constants.UpdateMarker))
            {
                string rawUpdateFound = File.ReadAllText(Constants.UpdateMarker);
                if (SemanticVersion.TryParse(rawUpdateFound, out ISemanticVersion updateFound))
                {
                    if (Constants.ApiVersion.IsPrerelease() && updateFound.IsNewerThan(Constants.ApiVersion))
                    {
                        this.Monitor.Log("A new version of SMAPI was detected last time you played.", LogLevel.Error);
                        this.Monitor.Log($"You can update to {updateFound}: https://smapi.io.", LogLevel.Error);
                        this.Monitor.Log("Press any key to continue playing anyway. (This only appears when using a SMAPI beta.)", LogLevel.Info);
                        Console.ReadKey();
                    }
                }
                File.Delete(Constants.UpdateMarker);
            }

            // show details if game crashed during last session
            if (File.Exists(Constants.FatalCrashMarker))
            {
                this.Monitor.Log("The game crashed last time you played. That can be due to bugs in the game, but if it happens repeatedly you can ask for help here: http://community.playstarbound.com/threads/108375/.", LogLevel.Error);
                this.Monitor.Log("If you ask for help, make sure to share your SMAPI log: https://log.smapi.io.", LogLevel.Error);
                this.Monitor.Log("Press any key to delete the crash data and continue playing.", LogLevel.Info);
                Console.ReadKey();
                File.Delete(Constants.FatalCrashLog);
                File.Delete(Constants.FatalCrashMarker);
            }

            // start game
            this.Monitor.Log("Starting game...", LogLevel.Debug);
            try
            {
                this.IsGameRunning = true;
                StardewValley.Program.releaseBuild = true; // game's debug logic interferes with SMAPI opening the game window
                this.GameInstance.Run();
            }
            catch (InvalidOperationException ex) when (ex.Source == "Microsoft.Xna.Framework.Xact" && ex.StackTrace.Contains("Microsoft.Xna.Framework.Audio.AudioEngine..ctor"))
            {
                this.Monitor.Log("The game couldn't load audio. Do you have speakers or headphones plugged in?", LogLevel.Error);
                this.Monitor.Log($"Technical details: {ex.GetLogSummary()}", LogLevel.Trace);
                this.PressAnyKeyToExit();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"The game failed unexpectedly: {ex.GetLogSummary()}", LogLevel.Error);
                this.PressAnyKeyToExit();
            }
            finally
            {
                this.Dispose();
            }
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            // skip if already disposed
            if (this.IsDisposed)
                return;
            this.IsDisposed = true;
            this.Monitor.Log("Disposing...", LogLevel.Trace);

            // dispose mod data
            foreach (IModMetadata mod in this.ModRegistry.GetAll())
            {
                try
                {
                    (mod.Mod as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    mod.LogAsMod($"Mod failed during disposal: {ex.GetLogSummary()}.", LogLevel.Warn);
                }
            }

            // dispose core components
            this.IsGameRunning = false;
            this.ConsoleManager?.Dispose();
            this.ContentCore?.Dispose();
            this.CancellationTokenSource?.Dispose();
            this.GameInstance?.Dispose();
            this.LogFile?.Dispose();

            // end game (moved from Game1.OnExiting to let us clean up first)
            Process.GetCurrentProcess().Kill();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Initialise SMAPI and mods after the game starts.</summary>
        private void InitialiseAfterGameStart()
        {
            // load settings
            this.GameInstance.VerboseLogging = this.Settings.VerboseLogging;

            // load core components
            this.DeprecationManager = new DeprecationManager(this.Monitor, this.ModRegistry);

            // redirect direct console output
            {
                Monitor monitor = this.GetSecondaryMonitor("game");
                if (monitor.WriteToConsole)
                    this.ConsoleManager.OnMessageIntercepted += message => this.HandleConsoleMessage(monitor, message);
            }

            // add headers
            if (this.Settings.DeveloperMode)
                this.Monitor.Log($"You configured SMAPI to run in developer mode. The console may be much more verbose. You can disable developer mode by installing the non-developer version of SMAPI, or by editing {Constants.ApiConfigPath}.", LogLevel.Info);
            if (!this.Settings.CheckForUpdates)
                this.Monitor.Log($"You configured SMAPI to not check for updates. Running an old version of SMAPI is not recommended. You can enable update checks by reinstalling SMAPI or editing {Constants.ApiConfigPath}.", LogLevel.Warn);
            if (!this.Monitor.WriteToConsole)
                this.Monitor.Log("Writing to the terminal is disabled because the --no-terminal argument was received. This usually means launching the terminal failed.", LogLevel.Warn);
            this.VerboseLog("Verbose logging enabled.");

            // validate XNB integrity
            if (!this.ValidateContentIntegrity())
                this.Monitor.Log("SMAPI found problems in your game's content files which are likely to cause errors or crashes. Consider uninstalling XNB mods or reinstalling the game.", LogLevel.Error);

            // load mod data
            ModToolkit toolkit = new ModToolkit();
            ModDatabase modDatabase = toolkit.GetModDatabase(Constants.ApiMetadataPath);

            // load mods
            {
                this.Monitor.Log("Loading mod metadata...", LogLevel.Trace);
                ModResolver resolver = new ModResolver();

                // load manifests
                IModMetadata[] mods = resolver.ReadManifests(toolkit, this.ModsPath, modDatabase).ToArray();
                resolver.ValidateManifests(mods, Constants.ApiVersion, toolkit.GetUpdateUrl);

                // process dependencies
                mods = resolver.ProcessDependencies(mods, modDatabase).ToArray();

                // load mods
                this.LoadMods(mods, this.Toolkit.JsonHelper, this.ContentCore, modDatabase);

                // write metadata file
                if (this.Settings.DumpMetadata)
                {
                    ModFolderExport export = new ModFolderExport
                    {
                        Exported = DateTime.UtcNow.ToString("O"),
                        ApiVersion = Constants.ApiVersion.ToString(),
                        GameVersion = Constants.GameVersion.ToString(),
                        ModFolderPath = this.ModsPath,
                        Mods = mods
                    };
                    this.Toolkit.JsonHelper.WriteJsonFile(Path.Combine(Constants.LogDir, $"{Constants.LogNamePrefix}metadata-dump.json"), export);
                }

                // check for updates
                this.CheckForUpdatesAsync(mods);
            }
            if (this.Monitor.IsExiting)
            {
                this.Monitor.Log("SMAPI shutting down: aborting initialisation.", LogLevel.Warn);
                return;
            }

            // update window titles
            int modsLoaded = this.ModRegistry.GetAll().Count();
            this.GameInstance.Window.Title = $"Stardew Valley {Constants.GameVersion} - running SMAPI {Constants.ApiVersion} with {modsLoaded} mods";
            Console.Title = $"SMAPI {Constants.ApiVersion} - running Stardew Valley {Constants.GameVersion} with {modsLoaded} mods";

            // start SMAPI console
            new Thread(this.RunConsoleLoop).Start();
        }

        /// <summary>Handle the game changing locale.</summary>
        private void OnLocaleChanged()
        {
            // get locale
            string locale = this.ContentCore.GetLocale();
            LocalizedContentManager.LanguageCode languageCode = this.ContentCore.Language;

            // update mod translation helpers
            foreach (IModMetadata mod in this.ModRegistry.GetAll(contentPacks: false))
                (mod.Mod.Helper.Translation as TranslationHelper)?.SetLocale(locale, languageCode);
        }

        /// <summary>Run a loop handling console input.</summary>
        [SuppressMessage("ReSharper", "FunctionNeverReturns", Justification = "The thread is aborted when the game exits.")]
        private void RunConsoleLoop()
        {
            // prepare console
            this.Monitor.Log("Type 'help' for help, or 'help <cmd>' for a command's usage", LogLevel.Info);
            this.GameInstance.CommandManager.Add("SMAPI", "help", "Lists command documentation.\n\nUsage: help\nLists all available commands.\n\nUsage: help <cmd>\n- cmd: The name of a command whose documentation to display.", this.HandleCommand);
            this.GameInstance.CommandManager.Add("SMAPI", "reload_i18n", "Reloads translation files for all mods.\n\nUsage: reload_i18n", this.HandleCommand);

            // start handling command line input
            Thread inputThread = new Thread(() =>
            {
                while (true)
                {
                    // get input
                    string input = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    // handle command
                    this.Monitor.LogUserInput(input);
                    this.GameInstance.CommandQueue.Enqueue(input);
                }
            });
            inputThread.Start();

            // keep console thread alive while the game is running
            while (this.IsGameRunning && !this.Monitor.IsExiting)
                Thread.Sleep(1000 / 10);
            if (inputThread.ThreadState == ThreadState.Running)
                inputThread.Abort();
        }

        /// <summary>Look for common issues with the game's XNB content, and log warnings if anything looks broken or outdated.</summary>
        /// <returns>Returns whether all integrity checks passed.</returns>
        private bool ValidateContentIntegrity()
        {
            this.Monitor.Log("Detecting common issues...", LogLevel.Trace);
            bool issuesFound = false;

            // object format (commonly broken by outdated files)
            {
                // detect issues
                bool hasObjectIssues = false;
                void LogIssue(int id, string issue) => this.Monitor.Log($@"Detected issue: item #{id} in Content\Data\ObjectInformation.xnb is invalid ({issue}).", LogLevel.Trace);
                foreach (KeyValuePair<int, string> entry in Game1.objectInformation)
                {
                    // must not be empty
                    if (string.IsNullOrWhiteSpace(entry.Value))
                    {
                        LogIssue(entry.Key, "entry is empty");
                        hasObjectIssues = true;
                        continue;
                    }

                    // require core fields
                    string[] fields = entry.Value.Split('/');
                    if (fields.Length < Object.objectInfoDescriptionIndex + 1)
                    {
                        LogIssue(entry.Key, "too few fields for an object");
                        hasObjectIssues = true;
                        continue;
                    }

                    // check min length for specific types
                    switch (fields[Object.objectInfoTypeIndex].Split(new[] { ' ' }, 2)[0])
                    {
                        case "Cooking":
                            if (fields.Length < Object.objectInfoBuffDurationIndex + 1)
                            {
                                LogIssue(entry.Key, "too few fields for a cooking item");
                                hasObjectIssues = true;
                            }
                            break;
                    }
                }

                // log error
                if (hasObjectIssues)
                {
                    issuesFound = true;
                    this.Monitor.Log(@"Your Content\Data\ObjectInformation.xnb file seems to be broken or outdated.", LogLevel.Warn);
                }
            }

            return !issuesFound;
        }

        /// <summary>Asynchronously check for a new version of SMAPI and any installed mods, and print alerts to the console if an update is available.</summary>
        /// <param name="mods">The mods to include in the update check (if eligible).</param>
        private void CheckForUpdatesAsync(IModMetadata[] mods)
        {
            if (!this.Settings.CheckForUpdates)
                return;

            new Thread(() =>
            {
                // create client
                string url = this.Settings.WebApiBaseUrl;
#if !SMAPI_FOR_WINDOWS
                url = url.Replace("https://", "http://"); // workaround for OpenSSL issues with the game's bundled Mono on Linux/Mac
#endif
                WebApiClient client = new WebApiClient(url, Constants.ApiVersion);
                this.Monitor.Log("Checking for updates...", LogLevel.Trace);

                // check SMAPI version
                ISemanticVersion updateFound = null;
                try
                {
                    ModEntryModel response = client.GetModInfo(new[] { new ModSearchEntryModel("Pathoschild.SMAPI", new[] { $"GitHub:{this.Settings.GitHubProjectName}" }) }).Single().Value;
                    ISemanticVersion latestStable = response.Main?.Version;
                    ISemanticVersion latestBeta = response.Optional?.Version;

                    if (latestStable == null && response.Errors.Any())
                    {
                        this.Monitor.Log("Couldn't check for a new version of SMAPI. This won't affect your game, but you may not be notified of new versions if this keeps happening.", LogLevel.Warn);
                        this.Monitor.Log($"Error: {string.Join("\n", response.Errors)}");
                    }
                    else if (this.IsValidUpdate(Constants.ApiVersion, latestBeta, this.Settings.UseBetaChannel))
                    {
                        updateFound = latestBeta;
                        this.Monitor.Log($"You can update SMAPI to {latestBeta}: {Constants.HomePageUrl}", LogLevel.Alert);
                    }
                    else if (this.IsValidUpdate(Constants.ApiVersion, latestStable, this.Settings.UseBetaChannel))
                    {
                        updateFound = latestStable;
                        this.Monitor.Log($"You can update SMAPI to {latestStable}: {Constants.HomePageUrl}", LogLevel.Alert);
                    }
                    else
                        this.Monitor.Log("   SMAPI okay.", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log("Couldn't check for a new version of SMAPI. This won't affect your game, but you won't be notified of new versions if this keeps happening.", LogLevel.Warn);
                    this.Monitor.Log(ex is WebException && ex.InnerException == null
                        ? $"Error: {ex.Message}"
                        : $"Error: {ex.GetLogSummary()}"
                    );
                }

                // show update message on next launch
                if (updateFound != null)
                    File.WriteAllText(Constants.UpdateMarker, updateFound.ToString());

                // check mod versions
                if (mods.Any())
                {
                    try
                    {
                        HashSet<string> suppressUpdateChecks = new HashSet<string>(this.Settings.SuppressUpdateChecks, StringComparer.InvariantCultureIgnoreCase);

                        // prepare search model
                        List<ModSearchEntryModel> searchMods = new List<ModSearchEntryModel>();
                        foreach (IModMetadata mod in mods)
                        {
                            if (!mod.HasID())
                                continue;

                            string[] updateKeys = mod.Manifest.UpdateKeys ?? new string[0];
                            searchMods.Add(new ModSearchEntryModel(mod.Manifest.UniqueID, updateKeys.Except(suppressUpdateChecks).ToArray()));
                        }

                        // fetch results
                        this.Monitor.Log($"   Checking for updates to {searchMods.Count} mods...", LogLevel.Trace);
                        IDictionary<string, ModEntryModel> results = client.GetModInfo(searchMods.ToArray());

                        // extract update alerts & errors
                        var updates = new List<Tuple<IModMetadata, ISemanticVersion, string>>();
                        var errors = new StringBuilder();
                        foreach (IModMetadata mod in mods.OrderBy(p => p.DisplayName))
                        {
                            // link to update-check data
                            if (!mod.HasID() || !results.TryGetValue(mod.Manifest.UniqueID, out ModEntryModel result))
                                continue;
                            mod.SetUpdateData(result);

                            // handle errors
                            if (result.Errors != null && result.Errors.Any())
                            {
                                errors.AppendLine(result.Errors.Length == 1
                                    ? $"   {mod.DisplayName}: {result.Errors[0]}"
                                    : $"   {mod.DisplayName}:\n      - {string.Join("\n      - ", result.Errors)}"
                                );
                            }

                            // parse versions
                            ISemanticVersion localVersion = mod.DataRecord?.GetLocalVersionForUpdateChecks(mod.Manifest.Version) ?? mod.Manifest.Version;
                            ISemanticVersion latestVersion = mod.DataRecord?.GetRemoteVersionForUpdateChecks(result.Main?.Version) ?? result.Main?.Version;
                            ISemanticVersion optionalVersion = mod.DataRecord?.GetRemoteVersionForUpdateChecks(result.Optional?.Version) ?? result.Optional?.Version;
                            ISemanticVersion unofficialVersion = result.Unofficial?.Version;

                            // show update alerts
                            if (this.IsValidUpdate(localVersion, latestVersion, useBetaChannel: true))
                                updates.Add(Tuple.Create(mod, latestVersion, result.Main?.Url));
                            else if (this.IsValidUpdate(localVersion, optionalVersion, useBetaChannel: localVersion.IsPrerelease()))
                                updates.Add(Tuple.Create(mod, optionalVersion, result.Optional?.Url));
                            else if (this.IsValidUpdate(localVersion, unofficialVersion, useBetaChannel: mod.Status == ModMetadataStatus.Failed))
                                updates.Add(Tuple.Create(mod, unofficialVersion, result.Unofficial?.Url));
                        }

                        // show update errors
                        if (errors.Length != 0)
                            this.Monitor.Log("Got update-check errors for some mods:\n" + errors.ToString().TrimEnd(), LogLevel.Trace);

                        // show update alerts
                        if (updates.Any())
                        {
                            this.Monitor.Newline();
                            this.Monitor.Log($"You can update {updates.Count} mod{(updates.Count != 1 ? "s" : "")}:", LogLevel.Alert);
                            foreach (var entry in updates)
                            {
                                IModMetadata mod = entry.Item1;
                                ISemanticVersion newVersion = entry.Item2;
                                string newUrl = entry.Item3;
                                this.Monitor.Log($"   {mod.DisplayName} {newVersion}: {newUrl}", LogLevel.Alert);
                            }
                        }
                        else
                            this.Monitor.Log("   All mods up to date.", LogLevel.Trace);
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log("Couldn't check for new mod versions. This won't affect your game, but you won't be notified of mod updates if this keeps happening.", LogLevel.Warn);
                        this.Monitor.Log(ex is WebException && ex.InnerException == null
                            ? ex.Message
                            : ex.ToString()
                        );
                    }
                }
            }).Start();
        }

        /// <summary>Get whether a given version should be offered to the user as an update.</summary>
        /// <param name="currentVersion">The current semantic version.</param>
        /// <param name="newVersion">The target semantic version.</param>
        /// <param name="useBetaChannel">Whether the user enabled the beta channel and should be offered pre-release updates.</param>
        private bool IsValidUpdate(ISemanticVersion currentVersion, ISemanticVersion newVersion, bool useBetaChannel)
        {
            return
                newVersion != null
                && newVersion.IsNewerThan(currentVersion)
                && (useBetaChannel || !newVersion.IsPrerelease());
        }

        /// <summary>Create a directory path if it doesn't exist.</summary>
        /// <param name="path">The directory path.</param>
        private void VerifyPath(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't create a path: {path}\n\n{ex.GetLogSummary()}", LogLevel.Error);
            }
        }

        /// <summary>Load and hook up the given mods.</summary>
        /// <param name="mods">The mods to load.</param>
        /// <param name="jsonHelper">The JSON helper with which to read mods' JSON files.</param>
        /// <param name="contentCore">The content manager to use for mod content.</param>
        /// <param name="modDatabase">Handles access to SMAPI's internal mod metadata list.</param>
        private void LoadMods(IModMetadata[] mods, JsonHelper jsonHelper, ContentCoordinator contentCore, ModDatabase modDatabase)
        {
            this.Monitor.Log("Loading mods...", LogLevel.Trace);

            HashSet<string> suppressUpdateChecks = new HashSet<string>(this.Settings.SuppressUpdateChecks, StringComparer.InvariantCultureIgnoreCase);
            IDictionary<IModMetadata, string[]> skippedMods = new Dictionary<IModMetadata, string[]>();
            void TrackSkip(IModMetadata mod, string userReasonPhrase, string devReasonPhrase = null) => skippedMods[mod] = new[] { userReasonPhrase, devReasonPhrase };

            // load content packs
            foreach (IModMetadata metadata in mods.Where(p => p.IsContentPack))
            {
                this.Monitor.Log($"   {metadata.DisplayName} (content pack, {PathUtilities.GetRelativePath(this.ModsPath, metadata.DirectoryPath)})...", LogLevel.Trace);

                // show warning for missing update key
                if (metadata.HasManifest() && !metadata.HasUpdateKeys())
                    metadata.SetWarning(ModWarning.NoUpdateKeys);

                // validate status
                if (metadata.Status == ModMetadataStatus.Failed)
                {
                    this.Monitor.Log($"      Failed: {metadata.Error}", LogLevel.Trace);
                    TrackSkip(metadata, metadata.Error);
                    continue;
                }

                // load mod as content pack
                IManifest manifest = metadata.Manifest;
                IMonitor monitor = this.GetSecondaryMonitor(metadata.DisplayName);
                IContentHelper contentHelper = new ContentHelper(this.ContentCore, metadata.DirectoryPath, manifest.UniqueID, metadata.DisplayName, monitor);
                IContentPack contentPack = new ContentPack(metadata.DirectoryPath, manifest, contentHelper, jsonHelper);
                metadata.SetMod(contentPack, monitor);
                this.ModRegistry.Add(metadata);
            }
            IModMetadata[] loadedContentPacks = this.ModRegistry.GetAll(assemblyMods: false).ToArray();

            // load mods
            {
                // get content packs by mod ID
                IDictionary<string, IContentPack[]> contentPacksByModID =
                    loadedContentPacks
                    .GroupBy(p => p.Manifest.ContentPackFor.UniqueID, StringComparer.InvariantCultureIgnoreCase)
                    .ToDictionary(
                            group => group.Key,
                            group => group.Select(metadata => metadata.ContentPack).ToArray(),
                            StringComparer.InvariantCultureIgnoreCase
                    );

                // load mods from metadata
                using (AssemblyLoader modAssemblyLoader = new AssemblyLoader(Constants.Platform, this.Monitor))
                {
                    InterfaceProxyFactory proxyFactory = new InterfaceProxyFactory();
                    foreach (IModMetadata metadata in mods.Where(p => !p.IsContentPack))
                    {
                        // get basic info
                        IManifest manifest = metadata.Manifest;
                        this.Monitor.Log(metadata.Manifest?.EntryDll != null
                            ? $"   {metadata.DisplayName} ({PathUtilities.GetRelativePath(this.ModsPath, metadata.DirectoryPath)}{Path.DirectorySeparatorChar}{metadata.Manifest.EntryDll})..." // don't use Path.Combine here, since EntryDLL might not be valid
                            : $"   {metadata.DisplayName}...", LogLevel.Trace);

                        // show warnings
                        if (metadata.HasManifest() && !metadata.HasUpdateKeys() && !suppressUpdateChecks.Contains(metadata.Manifest.UniqueID))
                            metadata.SetWarning(ModWarning.NoUpdateKeys);

                        // validate status
                        if (metadata.Status == ModMetadataStatus.Failed)
                        {
                            this.Monitor.Log($"      Failed: {metadata.Error}", LogLevel.Trace);
                            TrackSkip(metadata, metadata.Error);
                            continue;
                        }

                        // load mod
                        string assemblyPath = metadata.Manifest?.EntryDll != null
                            ? Path.Combine(metadata.DirectoryPath, metadata.Manifest.EntryDll)
                            : null;
                        Assembly modAssembly;
                        try
                        {
                            modAssembly = modAssemblyLoader.Load(metadata, assemblyPath, assumeCompatible: metadata.DataRecord?.Status == ModStatus.AssumeCompatible);
                        }
                        catch (IncompatibleInstructionException) // details already in trace logs
                        {
                            string[] updateUrls = new[] { modDatabase.GetModPageUrlFor(metadata.Manifest.UniqueID), "https://smapi.io/compat" }.Where(p => p != null).ToArray();

                            TrackSkip(metadata, $"it's no longer compatible. Please check for a new version at {string.Join(" or ", updateUrls)}.");
                            continue;
                        }
                        catch (SAssemblyLoadFailedException ex)
                        {
                            TrackSkip(metadata, $"it DLL couldn't be loaded: {ex.Message}");
                            continue;
                        }
                        catch (Exception ex)
                        {
                            TrackSkip(metadata, "its DLL couldn't be loaded.", $"Error: {ex.GetLogSummary()}");
                            continue;
                        }

                        // initialise mod
                        try
                        {
                            // get mod instance
                            if (!this.TryLoadModEntry(modAssembly, error => TrackSkip(metadata, error), out Mod mod))
                                continue;

                            // get content packs
                            if (!contentPacksByModID.TryGetValue(manifest.UniqueID, out IContentPack[] contentPacks))
                                contentPacks = new IContentPack[0];

                            // init mod helpers
                            IMonitor monitor = this.GetSecondaryMonitor(metadata.DisplayName);
                            IModHelper modHelper;
                            {
                                IModEvents events = new ModEvents(metadata, this.EventManager);
                                ICommandHelper commandHelper = new CommandHelper(manifest.UniqueID, metadata.DisplayName, this.GameInstance.CommandManager);
                                IContentHelper contentHelper = new ContentHelper(contentCore, metadata.DirectoryPath, manifest.UniqueID, metadata.DisplayName, monitor);
                                IDataHelper dataHelper = new DataHelper(manifest.UniqueID, metadata.DirectoryPath, jsonHelper);
                                IReflectionHelper reflectionHelper = new ReflectionHelper(manifest.UniqueID, metadata.DisplayName, this.Reflection, this.DeprecationManager);
                                IModRegistry modRegistryHelper = new ModRegistryHelper(manifest.UniqueID, this.ModRegistry, proxyFactory, monitor);
                                IMultiplayerHelper multiplayerHelper = new MultiplayerHelper(manifest.UniqueID, this.GameInstance.Multiplayer);
                                ITranslationHelper translationHelper = new TranslationHelper(manifest.UniqueID, manifest.Name, contentCore.GetLocale(), contentCore.Language);

                                IContentPack CreateTransitionalContentPack(string packDirPath, IManifest packManifest)
                                {
                                    IMonitor packMonitor = this.GetSecondaryMonitor(packManifest.Name);
                                    IContentHelper packContentHelper = new ContentHelper(contentCore, packDirPath, packManifest.UniqueID, packManifest.Name, packMonitor);
                                    return new ContentPack(packDirPath, packManifest, packContentHelper, this.Toolkit.JsonHelper);
                                }

                                modHelper = new ModHelper(manifest.UniqueID, metadata.DirectoryPath, this.Toolkit.JsonHelper, this.GameInstance.Input, events, contentHelper, commandHelper, dataHelper, modRegistryHelper, reflectionHelper, multiplayerHelper, translationHelper, contentPacks, CreateTransitionalContentPack, this.DeprecationManager);
                            }

                            // init mod
                            mod.ModManifest = manifest;
                            mod.Helper = modHelper;
                            mod.Monitor = monitor;

                            // track mod
                            metadata.SetMod(mod);
                            this.ModRegistry.Add(metadata);
                        }
                        catch (Exception ex)
                        {
                            TrackSkip(metadata, $"initialisation failed:\n{ex.GetLogSummary()}");
                        }
                    }
                }
            }
            IModMetadata[] loadedMods = this.ModRegistry.GetAll(contentPacks: false).ToArray();

            // log loaded mods
            this.Monitor.Log($"Loaded {loadedMods.Length} mods" + (loadedMods.Length > 0 ? ":" : "."), LogLevel.Info);
            foreach (IModMetadata metadata in loadedMods.OrderBy(p => p.DisplayName))
            {
                IManifest manifest = metadata.Manifest;
                this.Monitor.Log(
                    $"   {metadata.DisplayName} {manifest.Version}"
                    + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                    + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                    LogLevel.Info
                );
            }
            this.Monitor.Newline();

            // log loaded content packs
            if (loadedContentPacks.Any())
            {
                string GetModDisplayName(string id) => loadedMods.FirstOrDefault(p => id != null && id.Equals(p.Manifest?.UniqueID, StringComparison.InvariantCultureIgnoreCase))?.DisplayName;

                this.Monitor.Log($"Loaded {loadedContentPacks.Length} content packs:", LogLevel.Info);
                foreach (IModMetadata metadata in loadedContentPacks.OrderBy(p => p.DisplayName))
                {
                    IManifest manifest = metadata.Manifest;
                    this.Monitor.Log(
                        $"   {metadata.DisplayName} {manifest.Version}"
                        + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                        + (metadata.IsContentPack ? $" | for {GetModDisplayName(metadata.Manifest.ContentPackFor.UniqueID)}" : "")
                        + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                        LogLevel.Info
                    );
                }
                this.Monitor.Newline();
            }

            // log mod warnings
            this.LogModWarnings(this.ModRegistry.GetAll().ToArray(), skippedMods);

            // initialise translations
            this.ReloadTranslations(loadedMods);

            // initialise loaded non-content-pack mods
            foreach (IModMetadata metadata in loadedMods)
            {
                // add interceptors
                if (metadata.Mod.Helper.Content is ContentHelper helper)
                {
                    // ReSharper disable SuspiciousTypeConversion.Global
                    if (metadata.Mod is IAssetEditor editor)
                        helper.ObservableAssetEditors.Add(editor);
                    if (metadata.Mod is IAssetLoader loader)
                        helper.ObservableAssetLoaders.Add(loader);
                    // ReSharper restore SuspiciousTypeConversion.Global

                    this.ContentCore.Editors[metadata] = helper.ObservableAssetEditors;
                    this.ContentCore.Loaders[metadata] = helper.ObservableAssetLoaders;
                }

                // call entry method
                try
                {
                    IMod mod = metadata.Mod;
                    mod.Entry(mod.Helper);
                }
                catch (Exception ex)
                {
                    metadata.LogAsMod($"Mod crashed on entry and might not work correctly. Technical details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                // get mod API
                try
                {
                    object api = metadata.Mod.GetApi();
                    if (api != null && !api.GetType().IsPublic)
                    {
                        api = null;
                        this.Monitor.Log($"{metadata.DisplayName} provides an API instance with a non-public type. This isn't currently supported, so the API won't be available to other mods.", LogLevel.Warn);
                    }

                    if (api != null)
                        this.Monitor.Log($"   Found mod-provided API ({api.GetType().FullName}).", LogLevel.Trace);
                    metadata.SetApi(api);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Failed loading mod-provided API for {metadata.DisplayName}. Integrations with other mods may not work. Error: {ex.GetLogSummary()}", LogLevel.Error);
                }
            }

            // invalidate cache entries when needed
            // (These listeners are registered after Entry to avoid repeatedly reloading assets as mods initialise.)
            foreach (IModMetadata metadata in loadedMods)
            {
                if (metadata.Mod.Helper.Content is ContentHelper helper)
                {
                    helper.ObservableAssetEditors.CollectionChanged += (sender, e) =>
                    {
                        if (e.NewItems?.Count > 0)
                        {
                            this.Monitor.Log("Invalidating cache entries for new asset editors...", LogLevel.Trace);
                            this.ContentCore.InvalidateCacheFor(e.NewItems.Cast<IAssetEditor>().ToArray(), new IAssetLoader[0]);
                        }
                    };
                    helper.ObservableAssetLoaders.CollectionChanged += (sender, e) =>
                    {
                        if (e.NewItems?.Count > 0)
                        {
                            this.Monitor.Log("Invalidating cache entries for new asset loaders...", LogLevel.Trace);
                            this.ContentCore.InvalidateCacheFor(new IAssetEditor[0], e.NewItems.Cast<IAssetLoader>().ToArray());
                        }
                    };
                }
            }

            // reset cache now if any editors or loaders were added during entry
            IAssetEditor[] editors = loadedMods.SelectMany(p => p.Mod.Helper.Content.AssetEditors).ToArray();
            IAssetLoader[] loaders = loadedMods.SelectMany(p => p.Mod.Helper.Content.AssetLoaders).ToArray();
            if (editors.Any() || loaders.Any())
            {
                this.Monitor.Log("Invalidating cached assets for new editors & loaders...", LogLevel.Trace);
                this.ContentCore.InvalidateCacheFor(editors, loaders);
            }

            // unlock mod integrations
            this.ModRegistry.AreAllModsInitialised = true;
        }

        /// <summary>Write a summary of mod warnings to the console and log.</summary>
        /// <param name="mods">The loaded mods.</param>
        /// <param name="skippedMods">The mods which were skipped, along with the friendly and developer reasons.</param>
        private void LogModWarnings(IModMetadata[] mods, IDictionary<IModMetadata, string[]> skippedMods)
        {
            // get mods with warnings
            IModMetadata[] modsWithWarnings = mods.Where(p => p.Warnings != ModWarning.None).ToArray();
            if (!modsWithWarnings.Any() && !skippedMods.Any())
                return;

            // log intro
            {
                int count = modsWithWarnings.Union(skippedMods.Keys).Count();
                this.Monitor.Log($"Found {count} mod{(count == 1 ? "" : "s")} with warnings:", LogLevel.Info);
            }

            // log skipped mods
            if (skippedMods.Any())
            {
                this.Monitor.Log("   Skipped mods", LogLevel.Error);
                this.Monitor.Log("   " + "".PadRight(50, '-'), LogLevel.Error);
                this.Monitor.Log("      These mods could not be added to your game.", LogLevel.Error);
                this.Monitor.Newline();

                foreach (var pair in skippedMods.OrderBy(p => p.Key.DisplayName))
                {
                    IModMetadata mod = pair.Key;
                    string[] reason = pair.Value;

                    this.Monitor.Log($"      - {mod.DisplayName}{(mod.Manifest?.Version != null ? " " + mod.Manifest.Version.ToString() : "")} because {reason[0]}", LogLevel.Error);
                    if (reason[1] != null)
                        this.Monitor.Log($"        ({reason[1]})", LogLevel.Trace);
                }
                this.Monitor.Newline();
            }

            // log warnings
            if (modsWithWarnings.Any())
            {
                // issue block format logic
                void LogWarningGroup(ModWarning warning, LogLevel logLevel, string heading, params string[] blurb)
                {
                    IModMetadata[] matches = modsWithWarnings.Where(p => p.Warnings.HasFlag(warning)).ToArray();
                    if (!matches.Any())
                        return;

                    this.Monitor.Log("   " + heading, logLevel);
                    this.Monitor.Log("   " + "".PadRight(50, '-'), logLevel);
                    foreach (string line in blurb)
                        this.Monitor.Log("      " + line, logLevel);
                    this.Monitor.Newline();
                    foreach (IModMetadata match in matches)
                        this.Monitor.Log($"      - {match.DisplayName}", logLevel);
                    this.Monitor.Newline();
                }

                // supported issues
                LogWarningGroup(ModWarning.BrokenCodeLoaded, LogLevel.Error, "Broken mods",
                    "These mods have broken code, but you configured SMAPI to load them anyway. This may cause bugs,",
                    "errors, or crashes in-game."
                );
                LogWarningGroup(ModWarning.ChangesSaveSerialiser, LogLevel.Warn, "Changed save serialiser",
                    "These mods change the save serialiser. They may corrupt your save files, or make them unusable if",
                    "you uninstall these mods."
                );
                LogWarningGroup(ModWarning.PatchesGame, LogLevel.Info, "Patched game code",
                    "These mods directly change the game code. They're more likely to cause errors or bugs in-game; if",
                    "your game has issues, try removing these first. Otherwise you can ignore this warning."
                );
                LogWarningGroup(ModWarning.UsesUnvalidatedUpdateTick, LogLevel.Info, "Bypassed safety checks",
                    "These mods bypass SMAPI's normal safety checks, so they're more likely to cause errors or save",
                    "corruption. If your game has issues, try removing these first."
                );
                LogWarningGroup(ModWarning.NoUpdateKeys, LogLevel.Debug, "No update keys",
                    "These mods have no update keys in their manifest. SMAPI may not notify you about updates for these",
                    "mods. Consider notifying the mod authors about this problem."
                );
                LogWarningGroup(ModWarning.UsesDynamic, LogLevel.Debug, "Not crossplatform",
                    "These mods use the 'dynamic' keyword, and won't work on Linux/Mac."
                );
            }
        }

        /// <summary>Load a mod's entry class.</summary>
        /// <param name="modAssembly">The mod assembly.</param>
        /// <param name="onError">A callback invoked when loading fails.</param>
        /// <param name="mod">The loaded instance.</param>
        private bool TryLoadModEntry(Assembly modAssembly, Action<string> onError, out Mod mod)
        {
            mod = null;

            // find type
            TypeInfo[] modEntries = modAssembly.DefinedTypes.Where(type => typeof(Mod).IsAssignableFrom(type) && !type.IsAbstract).Take(2).ToArray();
            if (modEntries.Length == 0)
            {
                onError($"its DLL has no '{nameof(Mod)}' subclass.");
                return false;
            }
            if (modEntries.Length > 1)
            {
                onError($"its DLL contains multiple '{nameof(Mod)}' subclasses.");
                return false;
            }

            // get implementation
            mod = (Mod)modAssembly.CreateInstance(modEntries[0].ToString());
            if (mod == null)
            {
                onError("its entry class couldn't be instantiated.");
                return false;
            }

            return true;
        }

        /// <summary>Reload translations for all mods.</summary>
        /// <param name="mods">The mods for which to reload translations.</param>
        private void ReloadTranslations(IEnumerable<IModMetadata> mods)
        {
            JsonHelper jsonHelper = this.Toolkit.JsonHelper;
            foreach (IModMetadata metadata in mods)
            {
                if (metadata.IsContentPack)
                    throw new InvalidOperationException("Can't reload translations for a content pack.");

                // read translation files
                IDictionary<string, IDictionary<string, string>> translations = new Dictionary<string, IDictionary<string, string>>();
                DirectoryInfo translationsDir = new DirectoryInfo(Path.Combine(metadata.DirectoryPath, "i18n"));
                if (translationsDir.Exists)
                {
                    foreach (FileInfo file in translationsDir.EnumerateFiles("*.json"))
                    {
                        string locale = Path.GetFileNameWithoutExtension(file.Name.ToLower().Trim());
                        try
                        {
                            if (jsonHelper.ReadJsonFileIfExists(file.FullName, out IDictionary<string, string> data))
                                translations[locale] = data;
                            else
                                metadata.LogAsMod($"Mod's i18n/{locale}.json file couldn't be parsed.");
                        }
                        catch (Exception ex)
                        {
                            metadata.LogAsMod($"Mod's i18n/{locale}.json file couldn't be parsed: {ex.GetLogSummary()}");
                        }
                    }
                }

                // validate translations
                foreach (string locale in translations.Keys.ToArray())
                {
                    // skip empty files
                    if (translations[locale] == null || !translations[locale].Keys.Any())
                    {
                        metadata.LogAsMod($"Mod's i18n/{locale}.json is empty and will be ignored.", LogLevel.Warn);
                        translations.Remove(locale);
                        continue;
                    }

                    // handle duplicates
                    HashSet<string> keys = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                    HashSet<string> duplicateKeys = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                    foreach (string key in translations[locale].Keys.ToArray())
                    {
                        if (!keys.Add(key))
                        {
                            duplicateKeys.Add(key);
                            translations[locale].Remove(key);
                        }
                    }
                    if (duplicateKeys.Any())
                        metadata.LogAsMod($"Mod's i18n/{locale}.json has duplicate translation keys: [{string.Join(", ", duplicateKeys)}]. Keys are case-insensitive.", LogLevel.Warn);
                }

                // update translation
                TranslationHelper translationHelper = (TranslationHelper)metadata.Mod.Helper.Translation;
                translationHelper.SetTranslations(translations);
            }
        }

        /// <summary>The method called when the user submits a core SMAPI command in the console.</summary>
        /// <param name="name">The command name.</param>
        /// <param name="arguments">The command arguments.</param>
        private void HandleCommand(string name, string[] arguments)
        {
            switch (name)
            {
                case "help":
                    if (arguments.Any())
                    {
                        Command result = this.GameInstance.CommandManager.Get(arguments[0]);
                        if (result == null)
                            this.Monitor.Log("There's no command with that name.", LogLevel.Error);
                        else
                            this.Monitor.Log($"{result.Name}: {result.Documentation}\n(Added by {result.ModName}.)", LogLevel.Info);
                    }
                    else
                    {
                        string message = "The following commands are registered:\n";
                        IGrouping<string, string>[] groups = (from command in this.GameInstance.CommandManager.GetAll() orderby command.ModName, command.Name group command.Name by command.ModName).ToArray();
                        foreach (var group in groups)
                        {
                            string modName = group.Key;
                            string[] commandNames = group.ToArray();
                            message += $"{modName}:\n  {string.Join("\n  ", commandNames)}\n\n";
                        }
                        message += "For more information about a command, type 'help command_name'.";

                        this.Monitor.Log(message, LogLevel.Info);
                    }
                    break;

                case "reload_i18n":
                    this.ReloadTranslations(this.ModRegistry.GetAll(contentPacks: false));
                    this.Monitor.Log("Reloaded translation files for all mods. This only affects new translations the mods fetch; if they cached some text, it may not be updated.", LogLevel.Info);
                    break;

                default:
                    throw new NotSupportedException($"Unrecognise core SMAPI command '{name}'.");
            }
        }

        /// <summary>Redirect messages logged directly to the console to the given monitor.</summary>
        /// <param name="monitor">The monitor with which to log messages.</param>
        /// <param name="message">The message to log.</param>
        private void HandleConsoleMessage(IMonitor monitor, string message)
        {
            // detect exception
            LogLevel level = message.Contains("Exception") ? LogLevel.Error : LogLevel.Trace;

            // ignore suppressed message
            if (level != LogLevel.Error && this.SuppressConsolePatterns.Any(p => p.IsMatch(message)))
                return;

            // forward to monitor
            monitor.Log(message, level);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        private void PressAnyKeyToExit()
        {
            this.Monitor.Log("Game has ended. Press any key to exit.", LogLevel.Info);
            this.PressAnyKeyToExit(showMessage: false);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        /// <param name="showMessage">Whether to print a 'press any key to exit' message to the console.</param>
        private void PressAnyKeyToExit(bool showMessage)
        {
            if (showMessage)
                Console.WriteLine("Game has ended. Press any key to exit.");
            Thread.Sleep(100);
            Console.ReadKey();
            Environment.Exit(0);
        }

        /// <summary>Get a monitor instance derived from SMAPI's current settings.</summary>
        /// <param name="name">The name of the module which will log messages with this instance.</param>
        private Monitor GetSecondaryMonitor(string name)
        {
            return new Monitor(name, this.ConsoleManager, this.LogFile, this.CancellationTokenSource, this.Settings.ColorScheme)
            {
                WriteToConsole = this.Monitor.WriteToConsole,
                ShowTraceInConsole = this.Settings.DeveloperMode,
                ShowFullStampInConsole = this.Settings.DeveloperMode
            };
        }

        /// <summary>Log a message if verbose mode is enabled.</summary>
        /// <param name="message">The message to log.</param>
        private void VerboseLog(string message)
        {
            if (this.Settings.VerboseLogging)
                this.Monitor.Log(message, LogLevel.Trace);
        }

        /// <summary>Get the absolute path to the next available log file.</summary>
        private string GetLogPath()
        {
            // default path
            {
                FileInfo defaultFile = new FileInfo(Path.Combine(Constants.LogDir, $"{Constants.LogFilename}.{Constants.LogExtension}"));
                if (!defaultFile.Exists)
                    return defaultFile.FullName;
            }

            // get first disambiguated path
            for (int i = 2; i < int.MaxValue; i++)
            {
                FileInfo file = new FileInfo(Path.Combine(Constants.LogDir, $"{Constants.LogFilename}.player-{i}.{Constants.LogExtension}"));
                if (!file.Exists)
                    return file.FullName;
            }

            // should never happen
            throw new InvalidOperationException("Could not find an available log path.");
        }

        /// <summary>Delete all log files created by SMAPI.</summary>
        private void PurgeLogFiles()
        {
            DirectoryInfo logsDir = new DirectoryInfo(Constants.LogDir);
            if (!logsDir.Exists)
                return;

            foreach (FileInfo logFile in logsDir.EnumerateFiles())
            {
                if (logFile.Name.StartsWith(Constants.LogNamePrefix, StringComparison.InvariantCultureIgnoreCase))
                {
                    try
                    {
                        FileUtilities.ForceDelete(logFile);
                    }
                    catch (IOException)
                    {
                        // ignore file if it's in use
                    }
                }
            }
        }
    }
}
