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

        private static bool ffmpegUpdateChecked = false;
        private static bool ytDLPUpdateChecked = false;

        public static async Task InitializeAsync()
		{
			if (!Directory.Exists(baseFolder))
			{
				Directory.CreateDirectory(baseFolder);
			}

            if (!Directory.Exists(tempFolder))
            {
                Directory.CreateDirectory(tempFolder);
            }



            if (!File.Exists(ytDlpPath))
			{
				Logger.LogInfo("yt-dlp not found. Downloading...");
				await DownloadFileAsync(YTDLP_URL, ytDlpPath, 1);
                
				Logger.LogInfo("yt-dlp download finished.");
                ytDLPUpdateChecked = true;
			}
			else if (!ytDLPUpdateChecked)
			{
                Logger.LogInfo("yt-dlp found. Checking for updates...");

                DateTime localBuildDate = File.GetLastWriteTimeUtc(ytDlpPath);
                DateTime? latestReleaseDate = await GetLatestGithubReleaseDate(YTDLP_RELEASE_URL);

                if (latestReleaseDate != null && localBuildDate.AddHours(24) < latestReleaseDate)
                {
                    Logger.LogInfo("yt-dlp update found. Downloading...");


                    try
                    {
                        File.Delete(ytDlpPath);
						await DownloadFileAsync(YTDLP_URL, ytDlpPath, 1);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"Failed to delete old yt-dlp: {e.Message}");
                    }
                }
                else
                {
                    Logger.LogInfo(latestReleaseDate == null ? "yt-dlp release date failed to parse." : "yt-dlp up to date.");
                }
            }

			bool needsFFmpeg = !File.Exists(ffmpegBinPath) || !Directory.Exists(Path.GetDirectoryName(ffmpegBinPath));

			if (needsFFmpeg)
			{
				Logger.LogInfo("ffmpeg not found. Downloading and extracting...");
				await DownloadAndExtractFFmpegAsync();
			}
			else if (!ffmpegUpdateChecked)
			{
                Logger.LogInfo("ffmpeg found. Checking for updates...");

				DateTime localBuildDate = File.GetLastWriteTimeUtc(ffmpegBinPath);
                DateTime? latestReleaseDate = await GetLatestGithubReleaseDate(FFMPEG_RELEASE_URL);

                if (latestReleaseDate != null && localBuildDate.AddHours(24) < latestReleaseDate)
				{
                    Logger.LogInfo("ffmpeg update found. Downloading and extracting...");

                    await DownloadAndExtractFFmpegAsync();
                }
				else
				{
                    Logger.LogInfo(latestReleaseDate == null ? "ffmpeg release date failed to parse." : "ffmpeg up to date.");
                }
            }


            if (!File.Exists(ytDlpPath))
            {
				BaseListener.ReportDownloaderStatus(false);
                Logger.LogError($"yt-dlp executable was not found at {ytDlpPath}. Internet problem?");
            }
            else if (!File.Exists(ffmpegBinPath))
            {
				BaseListener.ReportDownloaderStatus(false);
                Logger.LogError($"ffmpeg executable was not found at {ffmpegBinPath} after extraction. Internet problem? Not on Windows problem?");
            }
            else
			{
                if (!ytDLPUpdateChecked || !ffmpegUpdateChecked)
					Logger.LogInfo("Yt-DL initialization complete.");	
			
				ytDLPUpdateChecked = true;
				ffmpegUpdateChecked = true;
                BaseListener.ReportDownloaderStatus(true);
            }
        }

		private static async Task DownloadFileAsync(string url, string destinationPath, int timeout)
		{
			using HttpClient client = new();
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(BoomBoxCartMod.modName, BoomBoxCartMod.modVersion));
            client.Timeout = TimeSpan.FromMinutes(timeout);

			try
			{
				byte[] data = await client.GetByteArrayAsync(url);
				File.WriteAllBytes(destinationPath, data);
			}
			catch (Exception e)
			{
				Logger.LogError(e.Message);
			}
		}

		private static async Task DownloadAndExtractFFmpegAsync()
		{
			string zipPath = Path.Combine(baseFolder, "ffmpeg.zip");

			try
			{
				if (Directory.Exists(ffmpegFolder))
				{
					try
					{
						Directory.Delete(ffmpegFolder, true);
						Directory.CreateDirectory(ffmpegFolder);
					}
					catch (Exception ex)
					{
						Logger.LogWarning($"Failed to clean ffmpeg folder: {ex.Message}");
					}
				}

				Logger.LogInfo($"Downloading FFmpeg from {FFMPEG_URL}...");

				await DownloadFileAsync(FFMPEG_URL, zipPath, 6);

				if (!File.Exists(zipPath))
				{
					throw new Exception("FFmpeg zip file not downloaded properly.");
				}

				Logger.LogInfo($"Downloaded FFmpeg zip file. Extracting...");

				ZipFile.ExtractToDirectory(zipPath, ffmpegFolder);

				File.Delete(zipPath);

				if (!File.Exists(ffmpegBinPath))
				{
					Logger.LogInfo("FFmpeg not found at expected path. Searching for ffmpeg.exe in extracted files...");

					string[] ffmpegFiles = Directory.GetFiles(ffmpegFolder, "ffmpeg.exe", SearchOption.AllDirectories);

					if (ffmpegFiles.Length > 0)
					{
						string newPath = ffmpegFiles[0];
						Logger.LogInfo($"Found ffmpeg.exe at: {newPath}");

						ffmpegBinPath = newPath;
					}
					else
					{
						Logger.LogError("ffmpeg.exe not found in extracted files. Uh oh!");
						throw new Exception("ffmpeg.exe not found in extracted files. Uh oh!");
					}
				}

				Logger.LogInfo("FFmpeg extracted successfully.");
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error downloading or extracting FFmpeg: {ex.Message}");
				throw;
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
					if (title == null)
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
					Arguments = $"--get-title {url}",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};

				//Logger.LogInfo($"Running yt-dlp to get title for: {url}");

				using (var process = new Process { StartInfo = psi })
				{
					var tcs = new TaskCompletionSource<string>();
					process.Start();

					string title = await process.StandardOutput.ReadToEndAsync();
					await process.StandardError.ReadToEndAsync();

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
						return "Unknown Title";
					}

					//title = title.Trim();
					//byte[] bytes = Encoding.Default.GetBytes(title);
					//title = Encoding.UTF8.GetString(bytes);

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