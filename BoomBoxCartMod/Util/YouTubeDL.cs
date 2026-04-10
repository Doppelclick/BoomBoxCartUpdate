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

namespace BoomBoxCartMod.Util
{
	public static class YoutubeDL
	{
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		private static readonly string baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "BoomboxedCart");
        private static readonly string tempFolder = Path.Combine(baseFolder, "temp");
        //private const string YtDLP_URL = "https://github.com/yt-dlp/yt-dlp/releases/download/2025.02.19/yt-dlp.exe";
        private const string YTDLP_RELEASE_URL = "https://api.github.com/repos/yt-dlp/yt-dlp-Builds/releases/latest";
        private const string YTDLP_URL = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
		private const string FFMPEG_RELEASE_URL = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
		private const string FFMPEG_URL = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
		private static readonly string ytDlpPath = Path.Combine(baseFolder, "yt-dlp.exe");
		private static readonly string ffmpegFolder = Path.Combine(baseFolder, "ffmpeg");
		private static string ffmpegBinPath = Path.Combine(ffmpegFolder, "ffmpeg-master-latest-win64-gpl", "bin", "ffmpeg.exe");
		private static readonly object initializationLock = new object();
		private static Task initializeTask = Task.CompletedTask;

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

			try
			{
				Directory.CreateDirectory(baseFolder);
				Directory.CreateDirectory(tempFolder);

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
					await DownloadAndExtractFFmpegAsync();
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
						await DownloadAndExtractFFmpegAsync();
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

		private static async Task DownloadAndExtractFFmpegAsync()
		{
			string zipPath = Path.Combine(baseFolder, "ffmpeg.zip");
			string stagingFolder = Path.Combine(tempFolder, "ffmpeg-staging-" + Guid.NewGuid().ToString("N"));

			try
			{
				if (File.Exists(zipPath))
				{
					File.Delete(zipPath);
				}

				if (Directory.Exists(stagingFolder))
				{
					Directory.Delete(stagingFolder, true);
				}

				Directory.CreateDirectory(stagingFolder);

				Logger.LogInfo($"Downloading FFmpeg from {FFMPEG_URL}...");

				await DownloadFileAsync(FFMPEG_URL, zipPath, 6);

				if (!File.Exists(zipPath))
				{
					throw new Exception("FFmpeg zip file not downloaded properly.");
				}

				Logger.LogInfo($"Downloaded FFmpeg zip file. Extracting...");

				ZipFile.ExtractToDirectory(zipPath, stagingFolder);

				File.Delete(zipPath);

				string stagedFfmpegPath = Directory.GetFiles(stagingFolder, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();

				if (string.IsNullOrWhiteSpace(stagedFfmpegPath))
				{
					Logger.LogError("ffmpeg.exe not found in extracted files. Uh oh!");
					throw new Exception("ffmpeg.exe not found in extracted files. Uh oh!");
				}

				if (Directory.Exists(ffmpegFolder))
				{
					Directory.Delete(ffmpegFolder, true);
				}

				Directory.Move(stagingFolder, ffmpegFolder);
				ffmpegBinPath = Directory.GetFiles(ffmpegFolder, "ffmpeg.exe", SearchOption.AllDirectories).First();

				Logger.LogInfo("FFmpeg extracted successfully.");
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error downloading or extracting FFmpeg: {ex.Message}");
				throw;
			}
			finally
			{
				if (File.Exists(zipPath))
				{
					File.Delete(zipPath);
				}

				if (Directory.Exists(stagingFolder))
				{
					Directory.Delete(stagingFolder, true);
				}
			}
		}

		public static async Task<string> DownloadAudioTitleAsync(string videoUrl)
		{
            await InitializeAsync();

            return await Task.Run(async () =>
			{
				try
				{
					string title = Boombox.GetSongTitle(videoUrl);
					if (string.IsNullOrWhiteSpace(title) || IsUnknownTitle(title))
					{
						title = await GetVideoTitleInternalAsync(videoUrl);
						if (string.IsNullOrEmpty(title))
						{
							title = "Unknown Title";
						}
					}

					//Logger.LogInfo($"Got video title downloaded: {title}");
					// remove \n and \r from title
					title = title.Replace("\n", "").Replace("\r", "");

					return title;
				}
				catch (Exception ex)
				{
					Logger.LogError($"Download Error: {ex}");
					Logger.LogError($"Stack Trace: {ex.StackTrace}");

					throw ex;
				}
			});
		}

		private static bool IsUnknownTitle(string title)
		{
			if (string.IsNullOrWhiteSpace(title))
			{
				return true;
			}

			string trimmedTitle = title.Trim();
			return trimmedTitle.Equals("Unknown Title", StringComparison.OrdinalIgnoreCase)
				|| trimmedTitle.StartsWith("Unknown Title (", StringComparison.OrdinalIgnoreCase);
		}


		/** @Return File Path to Audio Clip **/
		public static async Task<string> DownloadAudioAsync(string videoUrl, string videoTitle)
		{
			//await InitializeAsync(); Presume DownloadAudioTitleAsync was run before

			string folder = Path.Combine(tempFolder, Guid.NewGuid().ToString());
			Directory.CreateDirectory(folder);

			Logger.LogInfo($"Downloading audio for {videoUrl}...");

			return await Task.Run(async () =>
            {
				try
				{
					string quality = !Boombox.ApplyQualityToDownloads ? "192K" :
						Boombox.qualityLevel switch
							{
								0 => "32K",
								1 => "64K",
								2 => "96K",
								3 => "128K",
								_ => "192K",
							};

					string noIckySpecialCharsFileName = $"audio_{DateTime.Now.Ticks}.%(ext)s";
					string command = $"-x --audio-format mp3 --audio-quality {quality} --ffmpeg-location \"{ffmpegBinPath}\" --output \"{Path.Combine(folder, noIckySpecialCharsFileName)}\" {videoUrl}";


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

		private static async Task<string> GetVideoTitleInternalAsync(string url)
		{
			try
			{
				ProcessStartInfo psi = new ProcessStartInfo
				{
					FileName = ytDlpPath,
					Arguments = $"--skip-download --no-playlist --no-warnings --encoding utf-8 --print \"%(title)s\" \"{url}\"",
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

					string title = await process.StandardOutput.ReadToEndAsync();
					string error = await process.StandardError.ReadToEndAsync();

					var timeoutTask = Task.Delay(10000);
					var processExitTask = Task.Run(() => process.WaitForExit());

					if (await Task.WhenAny(processExitTask, timeoutTask) == timeoutTask)
					{
						try { process.Kill(); } catch { }
						Logger.LogWarning("yt-dlp title fetch timed out");
						return "Unknown Title (Timeout)";
					}

					if (process.ExitCode != 0)
					{
						Logger.LogError($"yt-dlp error code: {process.ExitCode}");
						if (!string.IsNullOrWhiteSpace(error))
						{
							Logger.LogWarning($"yt-dlp title fetch error: {error.Trim()}");
						}
						return "Unknown Title";
					}

					//title = title.Trim();
					//byte[] bytes = Encoding.Default.GetBytes(title);
					//title = Encoding.UTF8.GetString(bytes);

					title = title.Trim();
					Logger.LogInfo($"Got video title: {title}");

					if (!string.IsNullOrEmpty(title))
					{
						try
						{
							// trying to clean title
							title = new string(title.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());
						}
						catch (Exception ex)
						{
							Logger.LogWarning($"Error sanitizing title: {ex.Message}");
						}
					}

					return string.IsNullOrEmpty(title) ? "Unknown Title" : title;
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error getting video title: {ex.Message}");
				return "Unknown Title";
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

        [Serializable]
        private class GitHubRelease
        {
            public string published_at;
        }
    }
}