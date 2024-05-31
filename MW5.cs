using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using AutomationTool;
using UnrealBuildTool;
using Tools.DotNETCommon;
using Steamworks;

namespace MW5.Automation
{
	// This struct mirrors the FModInfo struct in ModUtils.h for packaging purposes.
	public class FModInfo
	{
		public string displayName { get; set; }
		public string description { get; set; }

		public UInt64 steamPublishedFileId { get; set; }

		public string steamModVisibility { get; set; }

		public int buildNumber { get; set; }
		public int steamLastSubmittedBuildNumber { get; set; }

	}


	[Help("UAT command to cook and package MW5 mods")]
	class PackageMW5Mod : BuildCommand
	{
		const string Mod_INFO_FILE_NAME = "mod.json";
		const string RESOURCES_FOLDER_NAME = "Resources";

		public string UE4Exe;				// UE4 Exe to use
		public string ProjectName;          // name of the project the mod is part of
		public FileReference ProjectFileReference; // File Reference to the project file we are building against
		public string ModName;              // name of the mod to cook/package
		public string ModPluginPath;        // root directory path to the mod plugin
		public string ReleaseVersion;       // Release version this Mod is based off
		public string ModSubstitutionPath;  // folder path for asset substitutions
		public string ModContentFolder;     // directory where new data asset content is stored
		public string ModInfoSourcePath;    // location that we stage the mod info file
		public string DeployDirectory;      // directory the mod should be output to after completion

		public bool bSkipPackaging;             // Should we skip packaging steps
		public bool bPublishToSteam;            // should the mod be published to steam
		public string SteamVisibility;		// DEPRECATED, we now read this variable directly from ModInfo.

		public string LogFolder;            // Folder where we save log files too
		public string IntermediateFolder;   // intermediate folder for temporary work

		public string PakSaveDirectory;
		public string StagingDirectory;
		public string IconPath;

		public FModInfo ModInfo;
		public int BuildNumber;

		public float MaxSubmitProgress;
		public override void ExecuteBuild()
		{
			LogInformation("************************* Calling PackageMW5Mod");

			SetupPathsAndSettings();

			// determine if we should skip staging files
			bool bCookFiles = true;
			if (File.Exists(ModInfoSourcePath) && bSkipPackaging)
			{
				bCookFiles = false;
			}

			if (bCookFiles)
			{
				// clear out the staging directory
				DeleteDirectory(StagingDirectory);

				//cook
				bool bAbleToCook = IsAbleToCook();
				if (bAbleToCook)
				{

					BeginCook();

					// unrealpak step
					BeginPak();

					// increment the build number before copying to the staging directory
					IncrementBuildNumber(ModInfoSourcePath);
				}
				else
				{
					IncrementBuildNumber(ModInfoSourcePath);
				}
			}

			if (DirectoryExists(PakSaveDirectory))
			{
				string IntermediatePakLocation = StagingDirectory + "/Paks";
				if (CopyDirectory_NoExceptions(PakSaveDirectory, IntermediatePakLocation))
				{
					LogInformation("************************* Pak file staged to " + IntermediatePakLocation);
				}
			}
			else
			{
				LogInformation("************************* Copy pak files skipped (no pak files detected)");
			}

			LogInformation("************************* Copying " + Mod_INFO_FILE_NAME + " to " + StagingDirectory);
			string ModInfoDestPath = StagingDirectory + "/" + Mod_INFO_FILE_NAME;
			CopyFile(ModInfoSourcePath, ModInfoDestPath);

			LogInformation("************************* Copying Resources folder to " + StagingDirectory);
			string ResourcesSourcePath = ModPluginPath + "/" + RESOURCES_FOLDER_NAME;
			string ResourcesDestPath = StagingDirectory + "/" + RESOURCES_FOLDER_NAME;
			CopyDirectory_NoExceptions(ResourcesSourcePath, ResourcesDestPath);

			CheckPublishToSteam();

			if (DeployDirectory.Length > 0)
			{
				LogInformation("************************* Deploying mod to " + DeployDirectory);
				DeleteDirectory(DeployDirectory);
				try
				{
					System.IO.Directory.Move(StagingDirectory, DeployDirectory);
				}
				catch (Exception Ex)
				{
					throw new AutomationException(String.Format("Failed to move mod files to deploy directory: {0}, {1}", DeployDirectory, Ex.Message));
				}
			}
		}

		public void SetupPathsAndSettings()
		{
			UE4Exe = ParseParamValue("UE4Exe", @"UE4Editor-cmd.exe");
			ProjectName = ParseParamValue("Project", null);
			ModName = ParseParamValue("ModName", null);
			ModPluginPath = ParseParamValue("PluginPath", null);
			ReleaseVersion = ParseParamValue("basedonreleaseversion", null);
			ModSubstitutionPath = ParseParamValue("ModSubstitionFolder", null);
			DeployDirectory = ParseParamValue("DeployDirectory", null);

			bSkipPackaging = ParseParamBool("SkipPackaging", false);
			bPublishToSteam = ParseParamBool("PublishToSteam", false);
			SteamVisibility = ParseParamValue("SteamVisibility", null);

			ModContentFolder = ModPluginPath + @"/Content";
			LogFolder = ModPluginPath + @"/Saved/Logs";

			ProjectFileReference = new FileReference(ProjectName);
			IntermediateFolder = ModPluginPath + @"/Intermediate";

			PakSaveDirectory = ModPluginPath + @"/Saved/Paks";
			StagingDirectory = IntermediateFolder + "/Staging/" + ModName;

			ModInfoSourcePath = ModPluginPath + "/" + Mod_INFO_FILE_NAME;
			IconPath = StagingDirectory + @"/Resources/Icon128.png";

			ModInfo = ReadModInfo(ModInfoSourcePath);
			BuildNumber = ModInfo.buildNumber;
		}

		public FModInfo ReadModInfo(string Path)
		{
			string ModInfoSourceJson = File.ReadAllText(ModInfoSourcePath);
			if (!String.IsNullOrEmpty(ModInfoSourceJson))
			{
				return fastJSON.JSON.Instance.ToObject<FModInfo>(ModInfoSourceJson);
			}

			return null;
		}

		public void IncrementBuildNumber(string ModInfoPath)
		{
			BuildNumber++;

			if (!File.Exists(ModInfoPath))
			{
				LogError("Cannot write steam published file id, no mod info file found at: {0} ", ModInfoPath);
				return;
			}

			string BuildNumberKey = "buildNumber";

			string InputJson = File.ReadAllText(ModInfoSourcePath);
			Dictionary<string, object> JsonDictionary = fastJSON.JSON.Instance.ToObject<Dictionary<string, object>>(InputJson);
			if (JsonDictionary.ContainsKey(BuildNumberKey))
			{
				JsonDictionary[BuildNumberKey] = BuildNumber;
			}
			string OutputJson = fastJSON.JSON.Instance.ToJSON(JsonDictionary);
			OutputJson = fastJSON.JSON.Instance.Beautify(OutputJson);
			File.WriteAllText(ModInfoPath, OutputJson);
		}

		public void WritePublishedFileId(string ModInfoPath, Steamworks.Data.PublishedFileId PublishedFileId)
		{
			if (!File.Exists(ModInfoPath))
			{
				LogError("Cannot write steam published file id, no mod info file found at: {0} ", ModInfoPath);
				return;
			}

			string SteamPublishedFileIdText = "steamPublishedFileId";

			string InputJson = File.ReadAllText(ModInfoSourcePath);
			Dictionary<string, object> JsonDictionary = fastJSON.JSON.Instance.ToObject<Dictionary<string, object>>(InputJson);
			if(JsonDictionary.ContainsKey(SteamPublishedFileIdText))
			{
				JsonDictionary[SteamPublishedFileIdText] = PublishedFileId.ToString();
			}

			string OutputJson = fastJSON.JSON.Instance.ToJSON(JsonDictionary);
			OutputJson = fastJSON.JSON.Instance.Beautify(OutputJson); 
			File.WriteAllText(ModInfoPath, OutputJson);

			//LogInformation("Output: {0}", OutputJson);
		}
		public void WriteSteamSubmitted(string ModInfoPath)
		{
			if (!File.Exists(ModInfoPath))
			{
				LogError("Cannot write submission, no mod info file found at: {0} ", ModInfoPath);
				return;
			}

			string SteamLastSubmittedBuildNumber = "steamLastSubmittedBuildNumber";

			string InputJson = File.ReadAllText(ModInfoSourcePath);
			Dictionary<string, object> JsonDictionary = fastJSON.JSON.Instance.ToObject<Dictionary<string, object>>(InputJson);

			if (JsonDictionary.ContainsKey(SteamLastSubmittedBuildNumber))
			{
				JsonDictionary[SteamLastSubmittedBuildNumber] = BuildNumber.ToString();
			}

			string OutputJson = fastJSON.JSON.Instance.ToJSON(JsonDictionary);
			OutputJson = fastJSON.JSON.Instance.Beautify(OutputJson);
			File.WriteAllText(ModInfoPath, OutputJson);

			LogInformation("Build {0} marked submitted to Steam.", BuildNumber);
		}

		public bool IsAbleToCook()
		{
			bool bAbleToCook = true;

			bool bValidCommandLine = false;
			bValidCommandLine |= ProjectName.Length > 0;
			bValidCommandLine |= ModName.Length > 0;
			bValidCommandLine |= ModPluginPath.Length > 0;
			bValidCommandLine |= ReleaseVersion.Length > 0;

			if (!bValidCommandLine)
			{
				LogInformation("************************* ERROR: Malformed command line parameters detected");
				LogInformation(string.Format("Project Name: {0}", ProjectName));
				LogInformation(string.Format("Mod Name: {0}", ModName));
				LogInformation(string.Format("Plugin Path: {0}", ModPluginPath));
				LogInformation(string.Format("Release Version: {0}", ReleaseVersion));

				bAbleToCook = false;
			}

			if (ProjectFileReference.FullName.Length <= 0)
			{
				LogInformation("************************* ERROR: Unable to locate Project File");
				LogInformation(string.Format("Project Name: {0}", ProjectName));

				bAbleToCook = false;
			}

			// check to see if we even have any content to cook
			bool bHasNewContentToCook = false;
			bool bHasReplacementContentToCook = false;

			string[] newAssets = FindFiles("*", true, ModContentFolder);
			bHasNewContentToCook |= newAssets.Length > 0;

			if (ModSubstitutionPath.Length > 0)
			{
				// check to see if there are any substitution files that need to be cooked
				string ModSubstitutionFolder = ModPluginPath + @"/" + ModSubstitutionPath + @"/";
				string[] substitutionAssets = FindFiles("*", true, ModSubstitutionFolder);

				bHasReplacementContentToCook |= substitutionAssets.Length > 0;
			}

			if (!bHasNewContentToCook && !bHasReplacementContentToCook)
			{
				LogInformation("************************* Warning: Mod has no content to cook");
				bAbleToCook = false;
			}

			return bAbleToCook;
		}

		void BeginCook()
		{
			LogInformation("************************* COOK STEP INTIATED");
			string CookLogFile = LogUtils.GetUniqueLogName(String.Format("{0}/{1}CookOutput.txt", LogFolder, ModName));
			string SafeCookLogFile = MakePathSafeToUseWithCommandLine(CookLogFile);
			string EditorEXEPath = UE4Exe;
			string CommandletParameters = @"-DLCName=" + MakePathSafeToUseWithCommandLine(ModName);
			if (ModSubstitutionPath.Length > 0)
			{
				CommandletParameters += @" -DLCSubstitutionsContentPath=" + MakePathSafeToUseWithCommandLine(ModSubstitutionPath);
			}
			CommandletParameters += @" -basedonreleaseversion=" + ReleaseVersion;
			//CommandletParameters += @" -errorOnEngineContentUse";	// allow this to cook even if the assetregistry.bin is out of date
			//CommandletParameters += @" -iterate -iterateshash";	// use iterative cooking
			CommandletParameters += @" -fileopenlog";
			CommandletParameters += @" -abslog=" + SafeCookLogFile;
			CommandletParameters += @" -stdout";
			CommandletParameters += @" -CrashForUAT";
			CommandletParameters += @" -unattended";
			CommandletParameters += @" -UTF8Output";
			CommandletParameters += @" -Unversioned";
			//CommandletParameters += @" -waitforattach";

			CommandUtils.CookCommandlet(ProjectFileReference, EditorEXEPath, null, null, "", null, @"WindowsNoEditor", CommandletParameters);
		}

		void BeginPak()
		{
			LogInformation("************************* UNREALPAK STEP INITIATED");

			// package step
			string SavedFolder = String.Format("{0}/Saved", ModPluginPath);
			string ModPakLogFile = String.Format("{0}/{1}PakOutput.txt", LogFolder, ModName);

			string PakResponseFile = String.Format("{0}/PakResponseFile.txt", IntermediateFolder);
			string SafePakResponseFile = MakePathSafeToUseWithCommandLine(PakResponseFile);

			string PakOutputFile = String.Format("{0}/{1}.pak", PakSaveDirectory, ModName);
			string SafePakOutputFile = MakePathSafeToUseWithCommandLine(PakOutputFile);

			string SafeIntermediateFolder = MakePathSafeToUseWithCommandLine(IntermediateFolder);
			if (!DirectoryExists(IntermediateFolder))
			{
				CreateDirectory(IntermediateFolder);
			}
			if (FileExists(PakResponseFile))
			{
				DeleteFile(PakResponseFile);
			}

			string SafePakSaveDirectory = MakePathSafeToUseWithCommandLine(PakSaveDirectory);
			if (!DirectoryExists(SafePakSaveDirectory))
			{
				CreateDirectory(PakSaveDirectory);
			}
			if (FileExists(PakOutputFile))
			{
				DeleteFile(PakOutputFile);
			}

			// Create pak response file
			string NewContentDirectory = String.Format("\"{0}/Saved/Cooked/WindowsNoEditor/MW5Mercs/Plugins/{1}/Content/*\" \"../../../MW5Mercs/Plugins/{1}/Content/\"", ModPluginPath, ModName);
			string SubstitutionContentDirectory = String.Format("\"{0}/Saved/Cooked/WindowsNoEditor/MW5Mercs/Plugins/{1}/{2}/*\" \"../../../MW5Mercs/Content/\"", ModPluginPath, ModName, ModSubstitutionPath);
			string WwiseDirectory = String.Format("\"{0}/{1}/WwiseAudio/*\" \"../../../MW5Mercs/Content/WwiseAudio/\"", ModPluginPath, ModSubstitutionPath);
			string ConfigDirectory = String.Format("\"{0}/Config/*\" \"../../../MW5Mercs/Config/\"", ModPluginPath);
			string AssetRegistry = String.Format("\"{0}/Saved/Cooked/WindowsNoEditor/MW5Mercs/Plugins/{1}/AssetRegistry.bin\" \"../../../MW5Mercs/Plugins/{1}/\"", ModPluginPath, ModName);
			string[] PakDirectories = new string[]
			{
				NewContentDirectory,
				SubstitutionContentDirectory,
				WwiseDirectory,
				ConfigDirectory,
				AssetRegistry
			};

			WriteAllLines(PakResponseFile, PakDirectories);

			FileReference UnrealPakFR;
			if (HostPlatform.Current.HostEditorPlatform == UnrealTargetPlatform.Win64)
			{
				UnrealPakFR = FileReference.Combine(CommandUtils.EngineDirectory, "Binaries", "Win64", "UnrealPak.exe");
			}
			else
			{
				UnrealPakFR = FileReference.Combine(CommandUtils.EngineDirectory, "Binaries", HostPlatform.Current.HostEditorPlatform.ToString(), "UnrealPak");
			}

			string SafeLogFile = MakePathSafeToUseWithCommandLine(LogUtils.GetUniqueLogName(ModPakLogFile));
			string PakCommandLine = String.Format("{0} -create={1} -multiprocess -abslob={2}", SafePakOutputFile, SafePakResponseFile, SafeLogFile);
			IProcessResult Results;
			using (ThreadPoolWorkQueue Queue = new ThreadPoolWorkQueue())
			{
				string UnrealPakExe = UnrealPakFR.FullName;

				Queue.Enqueue(() => Results = Run(UnrealPakExe, PakCommandLine, Options: ERunOptions.AppMustExist | ERunOptions.NoLoggingOfRunCommand));

				bool bComplete = false;
				while (!bComplete)
				{
					bComplete = Queue.Wait(10 * 1000);
					LogInformation("Mod PAK GENERATION: Waiting for UnrealPak processes to complete");
				}
			}
		}
		
		public void CheckPublishToSteam()
		{
			if (!bPublishToSteam)
			{
				return;
			}

			if(ModInfo == null)
			{
				LogError("Cannot publish to steam, mod info not specified.");
			}

			bool bCanConnect = false;
			try
			{
				const uint MW5AppId = 784080;
				SteamClient.Init(MW5AppId);
				bCanConnect = true;
			}
			catch (System.Exception e)
			{
				// Couldn't init for some reason (steam is closed etc)
				LogError("Mod Steam Submission: Could not initialize steam client. {0}", e.ToString());
			}

			if (bCanConnect)
			{
				try
				{
					Task T = ExecuteSteamSubmission();
					T.Wait();
				}
				catch (SystemException e)
				{
					LogError("CouldNotSubmit: {0}", e.ToString());
				}

			}
		}

		async Task ExecuteSteamSubmission()
		{
			Steamworks.Data.PublishedFileId PublishedFileId = ModInfo.steamPublishedFileId;
			if (PublishedFileId != 0)
			{
				LogInformation("Mod Steam Submission: Updating existing workshop item with PublishedFileId = {0}", PublishedFileId);
				Steamworks.Ugc.Editor ExistingItem = new Steamworks.Ugc.Editor(PublishedFileId);
				PublishedFileId = await UpdateItem(ExistingItem);
			}
			else
			{
				LogInformation("Mod Steam Submission: Creating new workshop item...", PublishedFileId);
				Steamworks.Ugc.Editor NewItem = Steamworks.Ugc.Editor.NewCommunityFile;
				PublishedFileId = await UpdateItem(NewItem);
				WritePublishedFileId(ModInfoSourcePath, PublishedFileId);
				LogInformation("Mod Steam Submission: New workshop item submitted with PublishedFileId = {0}", PublishedFileId);
			}
		}

		async Task<Steamworks.Data.PublishedFileId> UpdateItem(Steamworks.Ugc.Editor Item)
		{
			Item.WithTitle(ModInfo.displayName);
			Item.WithDescription(ModInfo.description);
			Item.WithMetaData("Mod");

			//LogInformation("PreviewIcon Path: {0} (Exists: {1})", IconPath, File.Exists(IconPath));
			Item.WithPreviewFile(IconPath);

			DirectoryInfo ContentDirectory = new DirectoryInfo(StagingDirectory);
			Item.WithContent(ContentDirectory);

			// Code for debugging Content submission to steam
			//LogInformation("Content Directory: {0} (Exists: {1})", ContentDirectory.FullName, ContentDirectory.Exists);
			//var txtFiles = ContentDirectory.EnumerateFiles("*", SearchOption.AllDirectories);
			//foreach (var currentFile in txtFiles)
			//{
			//	string fileName = currentFile.FullName;
			//	LogInformation("   File: {0}", fileName);
			//}


			if (ModInfo.steamModVisibility.Equals("Public", StringComparison.InvariantCultureIgnoreCase))
			{
				Item.WithPublicVisibility();
			}
			else if (ModInfo.steamModVisibility.Equals("FriendsOnly", StringComparison.InvariantCultureIgnoreCase))
			{
				Item.WithFriendsOnlyVisibility();
			}
			else
			{
				Item.WithPrivateVisibility();
			}

			Steamworks.Data.PublishedFileId FileId = 0;
			var progressIndicator = new Progress<float>(ReportProgress);
			int MAX_ATTEMPTS = 3;
			for (int Attempt = 1; Attempt < MAX_ATTEMPTS; ++Attempt)
			{
				LogInformation("Mod Steam Submission: Submitting mod to Steam (Attempt {0} of {1})...", Attempt, MAX_ATTEMPTS);

				MaxSubmitProgress = 0;
				var result = await Item.SubmitAsync(progressIndicator);
				if (result.Success)
				{
					LogInformation("  Steam submission succeeded");
					WriteSteamSubmitted(ModInfoSourcePath);

					FileId = result.FileId;
					break;
				}
				else
				{
					LogError("  Steam submission failed for file id {1}. Reason: {0} ({2}%)", result.Result, result.FileId, (MaxSubmitProgress * 100).ToString());
				}

			}

			return FileId;
		}
		void ReportProgress(float value)
		{
			MaxSubmitProgress = value;
			//LogInformation("Mod Steam Submission: Progress: {0}", value);
		}
	}


}
