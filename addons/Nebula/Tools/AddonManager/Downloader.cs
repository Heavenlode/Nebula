using System;
using Godot;

namespace Nebula.Internal.Editor
{
    [Tool]
    public partial class Downloader : Node
    {
        public event Action<string> DownloadCompleted;

        public event Action<string> DownloadFailed;

        private HttpRequest httpRequest;
        private string currentTargetDir;
        private string currentRepoName;

        public override void _Ready()
        {
            httpRequest = new HttpRequest();
            AddChild(httpRequest);
            httpRequest.RequestCompleted += OnDownloadCompleted;
        }

        public void DownloadAddon(string repoUrl, string branch, string targetDir, string repoName)
        {
            string zipUrl = $"{repoUrl}/archive/refs/heads/{branch}.zip";
            currentTargetDir = targetDir;
            currentRepoName = repoName;

            Error error = httpRequest.Request(zipUrl);
            if (error != Error.Ok)
            {
                DownloadFailed?.Invoke($"Failed to start download: {error}");
            }
        }

        private void OnDownloadCompleted(long result, long responseCode, string[] headers, byte[] body)
        {
            if (result != (long)HttpRequest.Result.Success)
            {
                DownloadFailed?.Invoke($"Failed to download: {result}");
                return;
            }

            try
            {
                var tempDir = DirAccess.CreateTemp("AddonTemp");
                string tempPath = tempDir.GetCurrentDir();
                
                // Temporarily write the zip to a file
                string zipPath = $"{tempPath}/repo.zip";
                using (var file = FileAccess.Open(zipPath, FileAccess.ModeFlags.Write))
                {
                    if (file == null)
                    {
                        throw new Exception($"Failed to create zip file at {zipPath}");
                    }
                    file.StoreBuffer(body);
                    file.Close();
                }

                // Step 2: Extract using ZIPReader
                string baseFolder = "";
                using (var zip = new ZipReader())
                {
                    Error error = zip.Open(zipPath);
                    if (error != Error.Ok)
                    {
                        throw new Exception($"Failed to open ZIP: {error}");
                    }

                    foreach (string file in zip.GetFiles())
                    {
                        // Get the base folder name from the first file
                        if (string.IsNullOrEmpty(baseFolder))
                        {
                            baseFolder = file.Split('/')[0];
                        }

                        // Skip if not in Source directory
                        if (!file.Contains("/Source/"))
                        {
                            continue;
                        }

                        // Skip if this is a directory (ends with /)
                        if (file.EndsWith("/"))
                        {
                            continue;
                        }

                        // Read the file from ZIP
                        byte[] fileData = zip.ReadFile(file);
                        
                        // Calculate relative path from Source
                        string relativePath = file.Substring(file.IndexOf("/Source/") + 8);
                        string finalTargetDir = $"{currentTargetDir}/{baseFolder.Replace("-main", "")}";
                        string targetPath = $"{finalTargetDir}/{relativePath}";
                        
                        // Ensure directory exists
                        string targetFileDir = targetPath.GetBaseDir();
                        if (!string.IsNullOrEmpty(targetFileDir))
                        {
                            DirAccess.MakeDirRecursiveAbsolute(targetFileDir);
                        }

                        // Write the file
                        using (var outFile = FileAccess.Open(targetPath, FileAccess.ModeFlags.Write))
                        {
                            if (outFile == null)
                            {
                                GD.PrintErr($"Failed to create file at {targetPath}");
                                continue;
                            }
                            outFile.StoreBuffer(fileData);
                            outFile.Close();
                        }
                    }

                    zip.Close();
                }

                // Cleanup
                DirAccess.RemoveAbsolute(tempPath);

                string finalPath = $"{currentTargetDir}/{baseFolder.Replace("-main", "")}";
                DownloadCompleted?.Invoke(finalPath);
            }
            catch (Exception e)
            {
                DownloadFailed?.Invoke(e.Message);
            }
        }
    }
}