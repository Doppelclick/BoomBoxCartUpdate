using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using System.Text;
using System.Security.Policy;
using System.Text.RegularExpressions;
using UnityEngine;
using System.Runtime.Remoting.Lifetime;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BoomBoxCartMod.Util
{
    public class SongInfo
    {
        [JsonProperty("title")]
        public string title { get; set; } = "Unknown Title";
        [JsonProperty("duration")]
        public double duration { get; set; } = -1;

		public SongInfo() {}
		public SongInfo(string title, double duration) { 
			this.title = title;
			this.duration = duration;
		}

		public bool IsInvalid()
		{
			return duration == -1 && (title.IsNullOrWhiteSpace() || title.Trim().StartsWith("Unknown Title"));
		}
    }


    public static class YoutubeDL
	{
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		private static readonly string baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "BoomboxedCart");
        private static readonly string tempFolder = Path.Combine(baseFolder, "temp");
        private static readonly string jsFolder = Path.Combine(Directory.GetCurrentDirectory(), "JavaScript");

        private const string JS_RELEASE_URL = "https://nodejs.org/dist/v26.1.0/node-v26.1.0-win-x64.zip";
        //private const string YtDLP_URL = "https://github.com/yt-dlp/yt-dlp/releases/download/2025.02.19/yt-dlp.exe";
        private const string YTDLP_RELEASE_URL = "https://api.github.com/repos/yt-dlp/yt-dlp-Builds/releases/latest";
        private const string YTDLP_URL = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
		private const string FFMPEG_RELEASE_URL = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
		private const string FFMPEG_URL = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

		private static readonly string jsRuntimePath = Path.Combine(jsFolder, "node-v26.1.0-win-x64/node.exe");
		private static readonly string ytDlpPath = Path.Combine(baseFolder, "yt-dlp.exe");
		private static readonly string ffmpegFolder = Path.Combine(baseFolder, "ffmpeg");
		private static string ffmpegBinPath = Path.Combine(ffmpegFolder, "ffmpeg-master-latest-win64-gpl", "bin", "ffmpeg.exe");
		private static readonly object initializationLock = new object();
		private static Task initializeTask = Task.CompletedTask;

		public static bool isInitializing = false;

		private static bool jsInstalled = false;
        private static bool ffmpegUpdateChecked = false;
        private static bool ytDLPUpdateChecked = false;
		private static bool isUpdatingResources = false;
		private static string resourceUpdateStatus = string.Empty;

		public static bool IsUpdatingResources => isUpdatingResources;
		public static string ResourceUpdateStatus => string.IsNullOrWhiteSpace(resourceUpdateStatus) ? "Updating resources..." : resourceUpdateStatus;

        public static Task InitializeAsync()
		{
			lock (initializationLock)
			{
				bool resourcesReady = File.Exists(ytDlpPath) && File.Exists(ffmpegBinPath);

				if (initializeTask != null && !initializeTask.IsCompleted)
				{
					return initializeTask;
				}

				if (resourcesReady && ytDLPUpdateChecked && ffmpegUpdateChecked)
				{
					return Task.CompletedTask;
				}

				initializeTask = InitializeInternalAsync();
				return initializeTask;
			}
		}

		private static async Task InitializeInternalAsync()
		{
			SetResourceUpdateStatus("Checking downloader resources...", true);
			isInitializing = true;

			try
			{
				Directory.CreateDirectory(baseFolder);
				Directory.CreateDirectory(tempFolder);
				Directory.CreateDirectory(jsFolder);

				if (!File.Exists(jsRuntimePath))
				{
                    SetResourceUpdateStatus("Updating resources: downloading nodeJS...", true);
                    Logger.LogInfo("nodeJS not found. Downloading...");
                    await DownloadAndExtractArchiveAsync(JS_RELEASE_URL, jsFolder, "node.exe", 6);
                    Logger.LogInfo("nodeJS download finished.");
                    jsInstalled = true;
				}
				else
				{
					jsInstalled = true;
				}

				if (!File.Exists(ytDlpPath))
				{
					SetResourceUpdateStatus("Updating resources: downloading yt-dlp...", true);
					Logger.LogInfo("yt-dlp not found. Downloading...");
					await DownloadFileAsync(YTDLP_URL, ytDlpPath, 1);
					Logger.LogInfo("yt-dlp download finished.");
				}
				else if (!ytDLPUpdateChecked)
				{
					SetResourceUpdateStatus("Updating resources: checking yt-dlp...", true);
					Logger.LogInfo("yt-dlp found. Checking for updates...");

					DateTime localBuildDate = File.GetLastWriteTimeUtc(ytDlpPath);
					DateTime? latestReleaseDate = await GetLatestGithubReleaseDate(YTDLP_RELEASE_URL);

					if (latestReleaseDate != null && localBuildDate.AddHours(24) < latestReleaseDate)
					{
						SetResourceUpdateStatus("Updating resources: updating yt-dlp...", true);
						Logger.LogInfo("yt-dlp update found. Downloading...");
						await DownloadFileAsync(YTDLP_URL, ytDlpPath, 1);
					}
					else
					{
						Logger.LogInfo(latestReleaseDate == null ? "yt-dlp release date failed to parse." : "yt-dlp up to date.");
					}
				}

				ytDLPUpdateChecked = true;

				bool needsFFmpeg = !File.Exists(ffmpegBinPath) || !Directory.Exists(Path.GetDirectoryName(ffmpegBinPath));

				if (needsFFmpeg)
				{
					SetResourceUpdateStatus("Updating resources: downloading FFmpeg...", true);
					Logger.LogInfo("ffmpeg not found. Downloading and extracting...");
					await DownloadAndExtractArchiveAsync(FFMPEG_URL, ffmpegFolder, "ffmpeg.exe", 6);
				}
				else if (!ffmpegUpdateChecked)
				{
					SetResourceUpdateStatus("Updating resources: checking FFmpeg...", true);
					Logger.LogInfo("ffmpeg found. Checking for updates...");

					DateTime localBuildDate = File.GetLastWriteTimeUtc(ffmpegBinPath);
					DateTime? latestReleaseDate = await GetLatestGithubReleaseDate(FFMPEG_RELEASE_URL);

					if (latestReleaseDate != null && localBuildDate.AddHours(24) < latestReleaseDate)
					{
						SetResourceUpdateStatus("Updating resources: updating FFmpeg...", true);
						Logger.LogInfo("ffmpeg update found. Downloading and extracting...");
                        await DownloadAndExtractArchiveAsync(FFMPEG_URL, ffmpegFolder, "ffmpeg.exe", 6);
                    }
                    else
					{
						Logger.LogInfo(latestReleaseDate == null ? "ffmpeg release date failed to parse." : "ffmpeg up to date.");
					}
				}

				ffmpegUpdateChecked = true;

				if (!File.Exists(ytDlpPath))
				{
					BaseListener.ReportDownloaderStatus(false);
					Logger.LogError($"yt-dlp executable was not found at {ytDlpPath}. Internet problem?");
					SetResourceUpdateStatus("Required media tools are unavailable.", false);
				}
				else if (!File.Exists(ffmpegBinPath))
				{
					BaseListener.ReportDownloaderStatus(false);
					Logger.LogError($"ffmpeg executable was not found at {ffmpegBinPath} after extraction. Internet problem? Not on Windows problem?");
					SetResourceUpdateStatus("Required media tools are unavailable.", false);
				}
				else
				{
					Logger.LogInfo("Yt-DL initialization complete.");
					BaseListener.ReportDownloaderStatus(true);
					SetResourceUpdateStatus(string.Empty, false);
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Downloader initialization failed: {ex.Message}");
				BaseListener.ReportDownloaderStatus(false);
				SetResourceUpdateStatus("Required media tools are unavailable.", false);
			}
			finally
			{
				isInitializing = false;
			}
        }

		private static void SetResourceUpdateStatus(string message, bool updating)
		{
			resourceUpdateStatus = message;
			isUpdatingResources = updating;
		}

		private static async Task DownloadFileAsync(string url, string destinationPath, int timeout)
		{
			using HttpClient client = new();
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(BoomBoxCartMod.modName, BoomBoxCartMod.modVersion));
            client.Timeout = TimeSpan.FromMinutes(timeout);
			string tempDownloadPath = destinationPath + ".download";

			try
			{
				byte[] data = await client.GetByteArrayAsync(url);
				Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));

				if (File.Exists(tempDownloadPath))
				{
					File.Delete(tempDownloadPath);
				}

				File.WriteAllBytes(tempDownloadPath, data);
				ReplaceFile(tempDownloadPath, destinationPath);
			}
			catch (Exception e)
			{
				Logger.LogError(e.Message);
				if (File.Exists(tempDownloadPath))
				{
					File.Delete(tempDownloadPath);
				}
				throw;
			}
		}

		private static void ReplaceFile(string sourcePath, string destinationPath)
		{
			if (File.Exists(destinationPath))
			{
				string backupPath = destinationPath + ".bak";

				if (File.Exists(backupPath))
				{
					File.Delete(backupPath);
				}

				File.Replace(sourcePath, destinationPath, backupPath, true);

				if (File.Exists(backupPath))
				{
					File.Delete(backupPath);
				}
			}
			else
			{
				File.Move(sourcePath, destinationPath);
			}
		}

        private static async Task<string> DownloadAndExtractArchiveAsync(
			string url,
			string installFolder,
			string executableName,
			int retries = 3
		) {
            string archivePath = Path.Combine(
                tempFolder,
                Path.GetFileName(url)
				);

            string stagingFolder = Path.Combine(
                tempFolder,
                "staging-" + Guid.NewGuid().ToString("N")
				);

            try
            {
				if (File.Exists(archivePath))
				{
					File.Delete(archivePath);
				}

				if (Directory.Exists(stagingFolder))
				{
					Directory.Delete(stagingFolder, true);
				}

                Directory.CreateDirectory(stagingFolder);

                Logger.LogDebug($"Downloading: {url}");

                await DownloadFileAsync(url, archivePath, retries);

                if (!File.Exists(archivePath))
                    throw new Exception("Archive download failed.");

                Logger.LogDebug("Extracting archive...");

                string extension = Path.GetExtension(archivePath).ToLowerInvariant();

                if (extension == ".zip")
                {
                    ZipFile.ExtractToDirectory(
                        archivePath,
                        stagingFolder);
                }
                else
                {
                    throw new Exception($"Unsupported archive type: {extension}");
                }

                File.Delete(archivePath);

                string executablePath =
                    Directory.GetFiles(
                        stagingFolder,
                        executableName,
                        SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    throw new Exception($"{executableName} not found after extraction.");
                }

                if (Directory.Exists(installFolder))
                    Directory.Delete(installFolder, true);

                Directory.Move(stagingFolder, installFolder);

                executablePath =
                    Directory.GetFiles(
                        installFolder,
                        executableName,
                        SearchOption.AllDirectories)
                    .First();

                Logger.LogDebug($"{executableName} installed successfully.");

                return executablePath;
            }
            finally
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);

                if (Directory.Exists(stagingFolder))
                    Directory.Delete(stagingFolder, true);
            }
        }

        public static async Task<SongInfo> DownloadAudioInfoAsync(string videoUrl)
		{
            await InitializeAsync();

            return await Task.Run(async () =>
			{
				try
				{
					SongInfo info = DownloadHelper.songInfo.ContainsKey(videoUrl) ? DownloadHelper.songInfo[videoUrl] : null;
					if (info == null || info.IsInvalid())
					{
						info = await GetVideoTitleInternalAsync(videoUrl);
						if (info == null || string.IsNullOrEmpty(info.title))
						{
							info = new SongInfo();
						}
					}

					//Logger.LogInfo($"Got video title downloaded: {title}");
					// remove \n and \r from title
					info.title = info.title.Replace("\n", "").Replace("\r", "");

					return info;
				}
				catch (Exception ex)
				{
					Logger.LogError($"Download Error: {ex}");
					Logger.LogError($"Stack Trace: {ex.StackTrace}");

                    throw;
                }
			});
		}


		/** @Return File Path to Audio Clip **/
		public static async Task<string> DownloadAudioAsync(string videoUrl, SongInfo info)
		{
			//await InitializeAsync(); Presume DownloadAudioTitleAsync was run before


            string folder = Path.Combine(tempFolder, Guid.NewGuid().ToString());
			Directory.CreateDirectory(folder);

			Logger.LogDebug($"Downloading audio for {videoUrl}...");

			return await Task.Run(async () =>
            {
				try
				{
					string quality = !Boombox.ApplyQualityToDownloads ? "192K" :
						AudioPlayer.GetQuality() switch
							{
								0 => "32K",
								1 => "64K",
								2 => "96K",
								3 => "128K",
								_ => "192K",
							};

					string noIckySpecialCharsFileName = $"audio_{DateTime.Now.Ticks}.%(ext)s";
					string options = "";

                    if (jsInstalled)
                    {
                        options += $" --js-runtimes node:\"{jsRuntimePath}\"";
                    }

                    if (Instance.CookiePassthrough.Value == BoomBoxCartMod.CookieUsage.FILE && !string.IsNullOrWhiteSpace(Instance.CookiePath.Value))
                    {
                        options += $" --cookies \"{Instance.CookiePath.Value}\"";
						Logger.LogDebug("Attempting to use cookies from file.");
                    }
                    else if (Instance.CookiePassthrough.Value == BoomBoxCartMod.CookieUsage.BROWSER)
                    {
                        options += $" --cookies-from-browser \"{Instance.Browser.Value}\"";
                        Logger.LogDebug("Attempting to use cookies from browser.");
                    }

                    string command = $"-x --audio-format mp3 --audio-quality {quality}{options} --ffmpeg-location \"{ffmpegBinPath}\" --output \"{Path.Combine(folder, noIckySpecialCharsFileName)}\" {videoUrl}";

                    ProcessStartInfo processInfo = new()
					{
						FileName = ytDlpPath,
						Arguments = command,
						RedirectStandardOutput = true,
						RedirectStandardError = true,
						UseShellExecute = false,
						CreateNoWindow = true,
						StandardOutputEncoding = Encoding.UTF8
					};

					using (Process process = Process.Start(processInfo))
					{
						if (process == null)
						{
							throw new Exception("Failed to start yt-dlp process.");
						}

						string output = await process.StandardOutput.ReadToEndAsync();
						string error = await process.StandardError.ReadToEndAsync();

						process.WaitForExit();

						//Logger.LogInfo($"yt-dlp Output: {output}");
						//Logger.LogError($"yt-dlp Error Stream: {error}");

						if (!string.IsNullOrEmpty(error))
						{
							Logger.LogInfo($"An error recorded in yt-dlp download, error is probably not fatal though");
						}

						if (process.ExitCode != 0)
						{
							throw new Exception($"yt-dlp download failed. Exit Code: {process.ExitCode}. Error: {error}");
						}
					}

					//Logger.LogInfo("Audio download complete.");

					await Task.Delay(1000);

					string audioFilePath = Directory.GetFiles(folder, "*.mp3").FirstOrDefault();
					if (audioFilePath == null)
					{
						string[] allFiles = Directory.GetFiles(folder);
						Logger.LogError($"No MP3 files found. Total files: {allFiles.Length}");
						foreach (string file in allFiles)
						{
							Logger.LogError($"Found file: {file}");
						}
						throw new Exception("Audio download failed. No MP3 file created.");
					}

					//Logger.LogInfo($"Successfully downloaded audio to: {audioFilePath}");
					return audioFilePath;
				}
				catch (Exception ex)
				{
					Logger.LogError($"Download Error: {ex}");
					Logger.LogError($"Stack Trace: {ex.StackTrace}");

					if (Directory.Exists(folder))
					{
						try
						{
							Directory.Delete(folder, true);
						}
						catch (Exception cleanupEx)
						{
							Logger.LogError($"Failed to clean up temp folder: {cleanupEx}");
						}
					}
					throw;
				}
			});
		}

		private static async Task<SongInfo> GetVideoTitleInternalAsync(string url)
		{
			try
			{
				ProcessStartInfo psi = new ProcessStartInfo
				{
					FileName = ytDlpPath,
					Arguments = $"--skip-download --no-playlist --no-warnings --encoding utf-8 --print \"{{\\\"title\\\":\\\"%(title)s\\\",\\\"duration\\\":%(duration)s}}\" \"{url}\"",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8
				};

				//Logger.LogInfo($"Running yt-dlp to get title for: {url}");

				using (var process = new Process { StartInfo = psi })
				{
					process.Start();

					string json = await process.StandardOutput.ReadToEndAsync();
					string error = await process.StandardError.ReadToEndAsync();

					var timeoutTask = Task.Delay(DownloadHelper.INITIAL_DOWNLOAD_TIMEOUT * 1000);
					var processExitTask = Task.Run(() => process.WaitForExit());

					if (await Task.WhenAny(processExitTask, timeoutTask) == timeoutTask)
					{
						try { process.Kill(); } catch { }
						Logger.LogWarning("yt-dlp title fetch timed out");
						return new SongInfo();
					}

					if (process.ExitCode != 0)
					{
						Logger.LogError($"yt-dlp error code: {process.ExitCode}");
						if (!string.IsNullOrWhiteSpace(error))
						{
							Logger.LogWarning($"yt-dlp title fetch error: {error.Trim()}");
						}
						return new SongInfo();
					}

					//title = title.Trim();
					//byte[] bytes = Encoding.Default.GetBytes(title);
					//title = Encoding.UTF8.GetString(bytes);

					SongInfo info = null;

                    try
					{
						//Logger.LogInfo($"Got video info: {json}");

                        info = JsonConvert.DeserializeObject<SongInfo>(json);

                        if (info != null && !string.IsNullOrEmpty(info.title) && info.duration != null)
						{
							try
							{
								// trying to clean title
								info.title = new string(info.title.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());
							}
							catch (Exception ex)
							{
								Logger.LogWarning($"Error sanitizing title: {ex.Message}");
							}
						}
					}
					catch (Exception ex)
					{
                        Logger.LogWarning($"Error converting audio info: {ex.Message}");
						return new SongInfo();
                    }
 
					return info == null ? new SongInfo() : info;
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error getting video title: {ex.Message}");
				return new SongInfo();
            }
		}

        private static async Task<DateTime?> GetLatestGithubReleaseDate(string url)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(BoomBoxCartMod.modName, BoomBoxCartMod.modVersion));
				client.Timeout = TimeSpan.FromSeconds(20);

				try
				{
					var jsonRaw = await client.GetStringAsync(url);

					var release = JsonUtility.FromJson<GitHubRelease>(jsonRaw);

					if (release != null && !string.IsNullOrEmpty(release.published_at))
					{
						return DateTime.Parse(release.published_at).ToUniversalTime();
					}
				}
				catch (Exception e) {
					Logger.LogError(e.Message);
					Logger.LogInfo($"Update check failed for url: {url}");
				}

                return null;
            }
        }

        public static bool CleanUp()
		{
            if (Directory.Exists(Path.GetDirectoryName(tempFolder)))
            {
                Directory.Delete(tempFolder, true);
            }

			return false;
        }

		public static bool Uninstall()
		{
			if (IsUpdatingResources)
			{
				Logger.LogWarning("Attempted to uninstall downloader while resource update in progress. Please wait until the update is complete and try again if you need to.");
				return false;
			}

			// TODO: Stop current downloads, currently leads to errors when used while downloading

			CleanUp();

			try
			{
				if (Directory.Exists(baseFolder))
				{
					Directory.Delete(baseFolder, true);
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error during uninstallation of dependencies: {ex.Message}");
				return false;
			}

			Logger.LogInfo("Downloader dependencies uninstalled successfully.");
			return true;
		}

		public static Task Reinstall()
		{
			if (!Uninstall())
			{
				return Task.FromResult(false);
			}

			Logger.LogInfo("Starting downloader dependencies reinstall.");

			initializeTask = InitializeInternalAsync();

			return initializeTask;
		}



        [Serializable]
        private class GitHubRelease
        {
            public string published_at;
        }
    }
}