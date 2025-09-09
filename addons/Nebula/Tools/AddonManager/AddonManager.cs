using Nebula.Tools;

using System;
using Godot;
using Godot.Collections;

namespace Nebula.Internal.Editor
{

    /// <summary>
    /// Addon Manager window for Nebula. Allows browsing, downloading,
    /// and installing addons from the central Nebula.Addons repository.
    /// Integrates with the Godot editor and updates .csproj if necessary.
    /// </summary>
    [Tool]
    public partial class AddonManager : Window
    {

        /// <summary>
        /// UI element that lists available addons fetched from the repository.
        /// </summary>
        [Export]
        public ItemList AddonList;

        /// <summary>
        /// UI element that displays details or README content for the selected addon.
        /// </summary>
        [Export]
        public RichTextLabel AddonDescription;

        private bool loaded = false;
        private HttpRequest httpRequest;
        private HttpRequest readmeRequest;
        private Dictionary<string, string> readmeCache = [];
        private Dictionary<int, Dictionary> addonData = [];

        private Downloader downloader;

        private const string REPO_LIST_URL = "https://raw.githubusercontent.com/Heavenlode/Nebula.Addons/refs/heads/main/repo-list.json";

        /// <summary>
        /// Initializes HTTP requests and downloader when the window is ready.
        /// </summary>
        public override void _Ready()
        {
            // Create and configure HTTPRequest nodes
            httpRequest = new HttpRequest();
            readmeRequest = new HttpRequest();
            AddChild(httpRequest);
            AddChild(readmeRequest);

            downloader = new Downloader();
            AddChild(downloader);
            downloader.DownloadCompleted += _onDownloadCompleted;
            downloader.DownloadFailed += _onDownloadFailed;

            httpRequest.RequestCompleted += _onRequestCompleted;
            readmeRequest.RequestCompleted += _onReadmeRequestCompleted;
        }

        /// <summary>
        /// Registers the Addons menu item in the Nebula tool submenu.
        /// </summary>
        public void SetPluginRoot(Main pluginRoot)
        {
            // pluginRoot.RegisterMenuItem("Addons", _OnAddonsMenuClicked);
        }

        /// <summary>
        /// Opens the Addon Manager popup and loads addons if not already loaded.
        /// </summary>
        private void _OnAddonsMenuClicked()
        {
            PopupCentered();
            if (!loaded)
            {
                _loadAddons();
            }
        }

        /// <summary>
        /// Installs the currently selected addon by downloading it to the addons directory.
        /// Updates the project .csproj file if needed.
        /// </summary>
        private void _onInstall()
        {
            if (!AddonList.IsAnythingSelected())
            {
                GD.PrintErr("No addon selected for installation");
                return;
            }

            int selectedIdx = AddonList.GetSelectedItems()[0];
            if (!addonData.ContainsKey(selectedIdx))
            {
                GD.PrintErr("Selected addon data not found");
                return;
            }

            var repo = addonData[selectedIdx];
            string repoUrl = repo["url"].AsString();
            string repoName = repoUrl.Split('/')[^1];

            // Get the project root directory and construct addons path
            string addonsDir = ProjectSettings.GlobalizePath("res://addons");

            // Ensure the addons directory exists
            if (!DirAccess.DirExistsAbsolute(addonsDir))
            {
                DirAccess.MakeDirRecursiveAbsolute(addonsDir);
            }

            // Show downloading status
            AddonDescription.Text = $"Installing {repoName}...";

            downloader.DownloadAddon(repoUrl, "main", addonsDir, repoName);
        }

        /// <summary>
        /// Starts fetching the addon repository list.
        /// </summary>
        private void _loadAddons()
        {
            // Clear existing items
            AddonList.Clear();

            // Start the download
            Error error = httpRequest.Request(REPO_LIST_URL);
            if (error != Error.Ok)
            {
                GD.PrintErr($"Failed to start addon list download: {error}");
            }
        }

        /// <summary>
        /// Displays addon details when selected, fetching README if not cached.
        /// </summary>
        private void _onItemSelected(int index)
        {
            if (!addonData.ContainsKey(index))
            {
                GD.PrintErr($"No addon data found for index {index}");
                return;
            }

            var repo = addonData[index];
            string url = repo["url"].AsString();
            string repoName = url.Split('/')[^1];

            // Check if we have a cached README
            if (readmeCache.ContainsKey(repoName))
            {
                AddonDescription.Text = readmeCache[repoName];
                return;
            }

            // Show loading message
            AddonDescription.Text = "Loading README...";

            // Construct README URL (assuming main branch and README.md in root)
            string readmeUrl = $"https://raw.githubusercontent.com/Heavenlode/{repoName}/main/README.md";

            Error error = readmeRequest.Request(readmeUrl);
            if (error != Error.Ok)
            {
                AddonDescription.Text = "Failed to fetch README";
                GD.PrintErr($"Failed to start README download: {error}");
            }
        }

        /// <summary>
        /// Callback for addon list request completion.
        /// Parses and populates the addon list UI.
        /// </summary>
        private void _onRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
        {
            if (result != (long)HttpRequest.Result.Success)
            {
                GD.PrintErr($"Failed to download addon list: {result}");
                return;
            }

            string jsonStr = System.Text.Encoding.UTF8.GetString(body);
            var json = new Json();
            Error parseResult = json.Parse(jsonStr);

            if (parseResult != Error.Ok)
            {
                GD.PrintErr($"Failed to parse addon list JSON: {json.GetErrorMessage()}");
                return;
            }

            var repoList = json.Data.AsGodotArray();
            for (int i = 0; i < repoList.Count; i++)
            {
                if (repoList[i].VariantType == Variant.Type.Dictionary)
                {
                    var repo = repoList[i].AsGodotDictionary();
                    if (repo.ContainsKey("url"))
                    {
                        string url = repo["url"].AsString();
                        string repoName = url.Split('/')[^1];
                        AddonList.AddItem(repoName);
                        addonData[i] = repo; // Store the full repo data
                    }
                }
            }

            loaded = true;
        }

        /// <summary>
        /// Callback for README request completion.
        /// Updates the addon description panel.
        /// </summary>
        private void _onReadmeRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
        {
            if (result != (long)HttpRequest.Result.Success || responseCode != 200)
            {
                AddonDescription.Text = "README not found";
                return;
            }

            string readmeContent = System.Text.Encoding.UTF8.GetString(body);

            // Get the currently selected item's repo name
            int selectedIdx = AddonList.GetSelectedItems()[0];
            string repoName = addonData[selectedIdx]["url"].AsString().Split('/')[^1];

            // Cache the README
            readmeCache[repoName] = readmeContent;

            // Only update the text if this is still the selected item
            if (AddonList.IsAnythingSelected() && AddonList.GetSelectedItems()[0] == selectedIdx)
            {
                AddonDescription.Text = readmeContent;
            }
        }

        /// <summary>
        /// Handles post-download installation steps: updating project .csproj if props exist.
        /// </summary>
        private void _onDownloadCompleted(string targetPath)
        {
            try
            {
                // Check if Addon.props exists in the installed addon
                string propsPath = $"{targetPath}/Addon.props";
                if (!FileAccess.FileExists(propsPath))
                {
                    AddonDescription.Text = "Successfully installed addon! (No .props file found)";
                    return;
                }

                // Find and modify the .csproj file
                string projectFile = ProjectSettings.GlobalizePath("res://") + ProjectSettings.GetSetting("dotnet/project/assembly_name") + ".csproj";
                if (!FileAccess.FileExists(projectFile))
                {
                    GD.PrintErr("Could not find .csproj file");
                    return;
                }

                // Read the current content
                string content;
                using (var file = FileAccess.Open(projectFile, FileAccess.ModeFlags.Read))
                {
                    content = file.GetAsText();
                }

                // Get the relative path to the props file from the project root
                string relativePropsPath = propsPath.Replace(ProjectSettings.GlobalizePath("res://"), "").Replace("/", "\\");
                string importLine = $"  <Import Project=\"{relativePropsPath}\" />";

                // Check if the import already exists
                if (content.Contains(importLine))
                {
                    AddonDescription.Text = "Successfully installed addon! (.props already imported)";
                    return;
                }

                // Insert the import before the closing Project tag
                int insertPos = content.LastIndexOf("</Project>");
                if (insertPos == -1)
                {
                    GD.PrintErr("Invalid .csproj file format");
                    return;
                }

                string newContent = content.Insert(insertPos, importLine + "\n");

                // Write the modified content back
                using (var file = FileAccess.Open(projectFile, FileAccess.ModeFlags.Write))
                {
                    file.StoreString(newContent);
                }

                AddonDescription.Text = "Successfully installed addon and updated .csproj!";
            }
            catch (Exception e)
            {
                GD.PrintErr($"Failed to update .csproj: {e.Message}");
                AddonDescription.Text = "Addon installed but failed to update .csproj";
            }

            // Refresh the project
            if (Engine.IsEditorHint())
            {
                EditorInterface.Singleton.GetResourceFilesystem().Scan();
            }
        }

        /// <summary>
        /// Handles failed addon downloads.
        /// </summary>
        private void _onDownloadFailed(string error)
        {
            GD.PrintErr($"Failed to install addon: {error}");
            AddonDescription.Text = $"Failed to install addon: {error}";
        }

        /// <summary>
        /// Closes the Addon Manager window.
        /// </summary>
        public void _OnCloseRequested()
        {
            Hide();
        }
    }
}
