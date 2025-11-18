using UnityEngine;
using System.Text.RegularExpressions;
using Photon.Pun;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using BoomBoxCartMod.Util;

namespace BoomBoxCartMod
{
    public class BoomboxUI : MonoBehaviourPun
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
        private static ManualLogSource Logger => Instance.logger;

        public PhotonView photonView;
        public static bool showUI = false;
        private string urlInput = "";

        // Time
        private bool isTimeSliderBeingDragged = false; // used to apply changes on slider release
        private int songIndexForTime = -2;
        private float songTimePerc = 0f;
        private float lastSentSongTimePerc = -1f;

        // Volume
        private float normalizedVolume = 0.3f; // norm volume is 0-1, actual volume is 0-maxVolumeLimit (in boombox.cs) ((1 is so loud))
        private float lastSentNormalizedVolume = 0.3f;
        private bool isVolumeSliderBeingDragged = false; // used to apply changes on slider release

        // Quality
        private int qualityLevel = 3;
        private string[] qualityLabels = new string[] { "REALLY Low (You Freak)", "Low", "Medium-Low", "Medium-High (Recommended)", "High" };
        private bool isQualitySliderBeingDragged = false;
        private int lastSentQualityLevel = 3;

        // GUI State
        private Rect windowRect;
        private Boombox boombox;
        private BoomboxController controller;
        private VisualEffects visualEffects;
        private Visualizer visualizer;

        // GUI Styles
        private GUIStyle windowStyle;
        private GUIStyle headerStyle;
        private GUIStyle buttonStyle;
        private GUIStyle smallButtonStyle;
        private GUIStyle textFieldStyle;
        private GUIStyle labelStyle;
        private GUIStyle sliderStyle;
        private GUIStyle statusStyle;
        private GUIStyle scrollViewStyle;
        private GUIStyle queueHeaderStyle; // New style for queue
        private GUIStyle queueEntryStyle; // New style for queue entries
        private GUIStyle currentSongStyle; // New style for current song in queue
        private Texture2D backgroundTexture;
        private Texture2D buttonTexture;
        private Texture2D sliderBackgroundTexture;
        private Texture2D sliderThumbTexture;
        private Texture2D textFieldBackgroundTexture;

        private Vector2 urlScrollPosition = Vector2.zero;
        private float textFieldVisibleWidth = 350;

        private string errorMessage = "";
        private float errorMessageTime = 0f;
        public string statusMessage = "";

        private CursorLockMode previousLockMode;
        private bool previousCursorVisible;
        private bool stylesInitialized = false;
        private Vector2 scrollPosition = Vector2.zero; // Main Scroll View
        private Vector2 queueScrollPosition = Vector2.zero; // New Queue Scroll View

        private string lastUrl = null;

        private void Awake()
        {
            try
            {
                boombox = GetComponent<Boombox>();
                if (boombox != null)
                {
                    photonView = boombox.photonView;
                }
                else
                {
                    Logger.LogError("BoomboxUI: Failed to find Boombox component");
                    photonView = GetComponent<PhotonView>();
                }

                controller = GetComponent<BoomboxController>();

                visualEffects = GetComponent<VisualEffects>();
                if (visualEffects == null)
                {
                    visualEffects = gameObject.AddComponent<VisualEffects>();
                }

                visualizer = GetComponent<Visualizer>();
                if (visualizer == null)
                {
                    visualizer = gameObject.AddComponent<Visualizer>();
                    visualizer.audioSource = boombox.audioSource;
                }

                if (photonView == null)
                {
                    Logger.LogError("BoomboxUI: Failed to find PhotonView component");
                }

                // INCREASE WINDOW WIDTH TO ACCOMMODATE QUEUE
                windowRect = new Rect(Screen.width / 2 - 400, Screen.height / 2 - 175, 800, 550);

                //Logger.LogInfo($"BoomboxUI initialized. Boombox: {boombox}, PhotonView: {photonView}, Controller: {controller}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in BoomboxUI.Awake: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Update, ShowUI, HideUI, IsUIVisible, UpdateStatus, UpdateStatusFromBoombox, SendVolumeUpdate, SendQualityUpdate
        private void Update()
        {
            if (Time.time > errorMessageTime && !string.IsNullOrEmpty(errorMessage))
            {
                errorMessage = "";
            }

            if (showUI && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) // || Keyboard.current[Instance.OpenUIKey.Value].wasPressedThisFrame TODO: Fix: Prevents the key from opening the UI
            {
                if (controller != null)
                {
                    //Logger.LogInfo($"Player {PhotonNetwork.LocalPlayer.ActorNumber} releasing boombox control");
                    controller.ReleaseControl();
                }
                else
                {
                    HideUI();
                }
            }

            if (isTimeSliderBeingDragged && Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
            {
                isTimeSliderBeingDragged = false;
                songIndexForTime = -2;
                songTimePerc = 0f;
                lastSentSongTimePerc = -1f;
                //Logger.LogInfo("Time slider released, sending time update");
                SendTimeUpdate();
            }

            if (isVolumeSliderBeingDragged && Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
            {
                isVolumeSliderBeingDragged = false;
                //Logger.LogInfo("Volume slider released, sending volume update");
                SendVolumeUpdate();
            }

            if (isQualitySliderBeingDragged && Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
            {
                isQualitySliderBeingDragged = false;
                //Logger.LogInfo("Quality slider released, sending qualtiy update");
                SendQualityUpdate();
            }
        }

        public void ShowUI()
        {
            if (!showUI)
            {
                if (boombox == null || photonView == null)
                {
                    Logger.LogError("Cannot show UI - boombox or photonView is null");
                    return;
                }

                showUI = true;

                previousLockMode = Cursor.lockState;
                previousCursorVisible = Cursor.visible;

                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None; // TODO: Does not work properly

                if (boombox != null)
                {
                    normalizedVolume = boombox.audioSource.volume / boombox.maxVolumeLimit;
                    lastSentNormalizedVolume = normalizedVolume;
                    qualityLevel = Boombox.qualityLevel;
                    lastSentQualityLevel = qualityLevel;
                }

                //Logger.LogInfo("BoomboxUI shown");

                UpdateStatusFromBoombox();
            }
        }

        public void UpdateStatusFromBoombox()
        {
            if (boombox != null)
            {
                if (boombox.downloadHelper.IsProcessingQueue() && boombox.currentSong != null && boombox.currentSong.GetAudioClip() == null)
                {
                    statusMessage = $"Downloading audio from {boombox.downloadHelper.GetCurrentDownloadUrl()}...";
                }
                else if (boombox.isPlaying && boombox.currentSong != null)
                {
                    statusMessage = $"Now playing: {boombox.currentSong.Title}";
                }
                else if (!string.IsNullOrEmpty(boombox.currentSong?.Url))
                {
                    statusMessage = $"Ready to play: {boombox.currentSong.Title}";
                }
                else
                {
                    statusMessage = "Ready to play music! Enter a Video URL";
                }
            }
        }

        private void SendTimeUpdate()
        {
            // only send if the time has actually changed
            if (songTimePerc != lastSentSongTimePerc)
            {
                lastSentSongTimePerc = songTimePerc;

                if (boombox?.GetCurrentSongIndex() == songIndexForTime && songIndexForTime != -1 && // Make sure the song has not changed in the meantime
                    boombox?.audioSource?.clip != null && boombox.audioSource.clip.length > 0)
                {
                    float actualTime = Math.Max(0f, Math.Min(songTimePerc * boombox.audioSource.clip.length, boombox.audioSource.clip.length));

                    // update local volume
                    //Logger.LogInfo($"Setting time locally to {actualTime}");
                    boombox.audioSource.time = actualTime;

                    // update volume for all others too
                    photonView?.RPC("SyncPlayback", RpcTarget.All, boombox.GetCurrentSongIndex(), (long)(Boombox.GetCurrentTimeMilliseconds() - Math.Round(actualTime * 1000f)), PhotonNetwork.LocalPlayer.ActorNumber);
                }
            }
        }

        private void SendVolumeUpdate()
        {
            // only send if the volume has actually changed
            if (normalizedVolume != lastSentNormalizedVolume)
            {
                lastSentNormalizedVolume = normalizedVolume;

                // update local volume
                if (boombox.audioSource != null)
                {
                    float actualVolume = normalizedVolume * boombox.maxVolumeLimit;
                    //Logger.LogInfo($"Setting volume locally to {actualVolume}");
                    boombox.audioSource.volume = actualVolume;
                }

                // update volume for all others too
                photonView?.RPC("UpdateVolume", RpcTarget.AllBuffered, normalizedVolume, PhotonNetwork.LocalPlayer.ActorNumber);
            }
        }

        private void SendQualityUpdate()
        {
            // only send if the quality has actually changed
            if (qualityLevel != lastSentQualityLevel)
            {
                lastSentQualityLevel = qualityLevel;

                // update local quality
                if (boombox != null)
                {
                    //Logger.LogInfo($"Setting quality locally to {qualityLevel}");
                    boombox.SetQuality(qualityLevel);
                }

                /* Should not be a shared value really
                // update qual for others too
                photonView?.RPC("UpdateQuality", RpcTarget.AllBuffered, qualityLevel, PhotonNetwork.LocalPlayer.ActorNumber);
                */
            }
        }

        public void HideUI()
        {
            if (showUI)
            {
                showUI = false;

                Cursor.lockState = previousLockMode;
                Cursor.visible = previousCursorVisible;

                //Logger.LogInfo("BoomboxUI hidden");
            }
        }

        public bool IsUIVisible()
        {
            return showUI;
        }

        public void UpdateStatus(string message)
        {
            statusMessage = message;
        }

        private Texture2D CreateColorTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        // MODIFIED InitializeStyles to add queue styles
        private void InitializeStyles()
        {
            if (stylesInitialized)
                return;

            backgroundTexture = CreateColorTexture(new Color(0.1f, 0.1f, 0.1f, 0.9f));
            buttonTexture = CreateColorTexture(new Color(0.2f, 0.2f, 0.3f, 1f));
            sliderBackgroundTexture = CreateColorTexture(new Color(0.15f, 0.15f, 0.2f, 1f));
            sliderThumbTexture = CreateColorTexture(new Color(0.7f, 0.7f, 0.8f, 1f));
            textFieldBackgroundTexture = CreateColorTexture(new Color(0.15f, 0.17f, 0.2f, 1f));

            windowStyle = new GUIStyle(GUI.skin.window);
            windowStyle.normal.background = backgroundTexture;
            windowStyle.onNormal.background = backgroundTexture;
            windowStyle.border = new RectOffset(10, 10, 10, 10);
            windowStyle.padding = new RectOffset(15, 15, 20, 15);

            headerStyle = new GUIStyle(GUI.skin.label);
            headerStyle.fontSize = 18;
            headerStyle.fontStyle = FontStyle.Bold;
            headerStyle.normal.textColor = Color.white;
            headerStyle.alignment = TextAnchor.MiddleCenter;
            headerStyle.margin = new RectOffset(0, 0, 10, 20);

            buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.normal.background = buttonTexture;
            buttonStyle.hover.background = CreateColorTexture(new Color(0.3f, 0.3f, 0.4f, 1f));
            buttonStyle.active.background = CreateColorTexture(new Color(0.4f, 0.4f, 0.5f, 1f));
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.active.textColor = Color.white;
            buttonStyle.fontSize = 14;
            buttonStyle.padding = new RectOffset(15, 15, 8, 8);
            buttonStyle.margin = new RectOffset(5, 5, 5, 5);
            buttonStyle.alignment = TextAnchor.MiddleCenter;

            smallButtonStyle = new GUIStyle(buttonStyle);
            smallButtonStyle.padding = new RectOffset(8, 8, 4, 4);
            smallButtonStyle.fontSize = 12;

            textFieldStyle = new GUIStyle(GUI.skin.textField);
            textFieldStyle.normal.background = textFieldBackgroundTexture;
            textFieldStyle.normal.textColor = new Color(1f, 1f, 1f);
            textFieldStyle.fontSize = 14;
            textFieldStyle.padding = new RectOffset(10, 10, 8, 8);

            scrollViewStyle = new GUIStyle(GUI.skin.scrollView);
            scrollViewStyle.normal.background = textFieldBackgroundTexture;
            scrollViewStyle.border = new RectOffset(2, 2, 2, 2);
            scrollViewStyle.padding = new RectOffset(0, 0, 0, 0);

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.normal.textColor = Color.white;
            labelStyle.fontSize = 14;
            labelStyle.margin = new RectOffset(0, 0, 10, 5);

            statusStyle = new GUIStyle(GUI.skin.label);
            statusStyle.normal.textColor = Color.cyan;
            statusStyle.fontSize = 14;
            statusStyle.wordWrap = true;
            statusStyle.alignment = TextAnchor.MiddleCenter;

            sliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
            sliderStyle.normal.background = sliderBackgroundTexture;

            queueHeaderStyle = new GUIStyle(headerStyle);
            queueHeaderStyle.fontSize = 16;
            queueHeaderStyle.alignment = TextAnchor.MiddleLeft;
            queueHeaderStyle.margin = new RectOffset(5, 0, 10, 5);

            queueEntryStyle = new GUIStyle(textFieldStyle);
            queueEntryStyle.normal.background = CreateColorTexture(new Color(0.1f, 0.1f, 0.1f, 0.7f));
            queueEntryStyle.hover.background = CreateColorTexture(new Color(0.2f, 0.2f, 0.2f, 0.8f));
            queueEntryStyle.alignment = TextAnchor.MiddleLeft;

            currentSongStyle = new GUIStyle(queueEntryStyle);
            currentSongStyle.normal.background = CreateColorTexture(new Color(0.1f, 0.3f, 0.1f, 0.9f)); // Greenish for Current
            currentSongStyle.normal.textColor = Color.yellow;
            currentSongStyle.hover.background = currentSongStyle.normal.background;

            stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!showUI)
                return;

            if (!stylesInitialized)
                InitializeStyles();

            // Draw the main window
            windowRect = GUILayout.Window(0, windowRect, DrawUI, "Boombox Controller", windowStyle);
        }

        // Modified DrawUI to implement two-column layout
        private void DrawUI(int windowID)
        {
            // Title Header
            GUILayout.Label($"Control The Boombox In The Cart{(Boombox.audioMuted ? $" - MUTED({Instance.GlobalMuteKey.Value.ToString()})" : "")}", headerStyle);

            // Main Horizontal Layout for Controls (Left) and Queue (Right)
            GUILayout.BeginHorizontal();

                    // --- LEFT COLUMN: CONTROLS (W 400) ---
                    GUILayout.BeginVertical(GUILayout.Width(400));
                    DrawMainPanel(boombox);
                    GUILayout.EndVertical();

                    // --- RIGHT COLUMN: QUEUE DISPLAY (W 420) ---
                    GUILayout.BeginVertical(GUILayout.Width(420), GUILayout.ExpandHeight(true));
                    DrawQueue(boombox);
                    GUILayout.EndVertical(); // End Right Column Vertical

            GUILayout.EndHorizontal(); // End Main Horizontal

                    // Close Button (Stretched across bottom)
                    GUILayout.FlexibleSpace();
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Close", buttonStyle, GUILayout.Width(300), GUILayout.Height(36)))
                    {
                        if (controller != null)
                        {
                            controller.ReleaseControl();
                        }
                        else
                        {
                            HideUI();
                        }
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

            GUI.DragWindow(); // Enable dragging of the window
        }


        // Draw the Main Panel
        private void DrawMainPanel(Boombox boombox) // TODO: Fix inability to change playback time sometimes
        {
            scrollPosition = GUILayout.BeginScrollView(scrollPosition, false, false, GUILayout.ExpandHeight(true));
                

                // URL Input Section
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Enter Video URL:", labelStyle, GUILayout.ExpandWidth(true));

                if (GUILayout.Button("Clear", smallButtonStyle, GUILayout.Width(60)))
                {
                    urlInput = "";
                    GUI.FocusControl(null);
                }
                GUILayout.EndHorizontal();


                GUILayout.BeginHorizontal();
                urlScrollPosition = GUILayout.BeginScrollView(
                    urlScrollPosition,
                    false,
                    false,
                    GUILayout.Height(60)
                ); // TODO: Allow scrolling sideways

                urlInput = GUILayout.TextField(urlInput, textFieldStyle, GUILayout.Height(34));
                urlInput = Regex.Replace(urlInput, @"\s+", "");

                GUILayout.EndScrollView();
                GUILayout.EndHorizontal();


                // Song Time Slider Section
                //GUILayout.Space(5);

                float currentTime = 0f;
                float songLength = 0f;
                float timeDisplayPercentage = 0f;
                string songLengthString = "??";

                bool audioAvailable = boombox != null && boombox.audioSource?.clip != null;

                if (audioAvailable)
                {
                    currentTime = boombox.audioSource.time;
                    songLength = boombox.audioSource.clip.length;

                    if (songLength > 0)
                    {
                        timeDisplayPercentage = currentTime / songLength;
                        songLengthString = PrintTime(songLength);
                    }
                    else
                    {
                        audioAvailable = false;
                    }
                }

                GUILayout.Label($"{PrintTime(currentTime)} / {songLengthString}", labelStyle);

                GUILayout.BeginHorizontal();

                // check if the user started dragging the slider
                if (audioAvailable && Event.current.type == EventType.MouseDown &&
                    GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                {
                    isTimeSliderBeingDragged = true;
                }

                float newTimePercentage = GUILayout.HorizontalSlider(timeDisplayPercentage, 0f, 1f, sliderStyle, GUI.skin.horizontalSliderThumb);

                int songIndex = boombox.GetCurrentSongIndex();

                if (audioAvailable && songIndex != -1)
                {
                    // if volume changed and we weren't already dragging, start tracking drag
                    if (newTimePercentage != timeDisplayPercentage)
                    {
                        if (!isTimeSliderBeingDragged) // Unnecessary check
                        {
                            isTimeSliderBeingDragged = true;
                        }
                        if (songIndexForTime == -2)
                        {
                            songIndexForTime = songIndex;
                        }

                        if (songIndexForTime == songIndex)
                        {
                            // update time for immediate feedback while sliding
                            float actualTime = Math.Max(0f, Math.Min(newTimePercentage * songLength, boombox.audioSource.clip.length));
                            boombox.audioSource.time = actualTime;

                            songTimePerc = newTimePercentage;
                        }
                    }
                }

                GUILayout.EndHorizontal();


                // Main Control Buttons
                //GUILayout.Space(10);
                GUILayout.BeginHorizontal();

                // Rewind-Button (10 seconds back)
                if (GUILayout.Button("<<", smallButtonStyle, GUILayout.Width(40), GUILayout.Height(40)))
                {
                    if (boombox != null && boombox.audioSource?.clip != null)
                    {
                        photonView?.RPC(
                            "SyncPlayback",
                            RpcTarget.All,
                            boombox.GetCurrentSongIndex(),
                            (long)(Boombox.GetCurrentTimeMilliseconds() - (Math.Round(boombox.audioSource.time * 1000f) - 10000)),
                            PhotonNetwork.LocalPlayer.ActorNumber
                        );
                    }
                }


                // PLAY/QUEUE-Button
                string playButtonText = boombox != null && boombox.playbackQueue.Count > 0 ? "+ ENQUEUE" : "▶ PLAY";
                if (GUILayout.Button(playButtonText, buttonStyle, GUILayout.Height(40)))
                {
                (string cleanedUrl, int seconds) = IsValidVideoUrl(urlInput);
                    if (string.IsNullOrEmpty(cleanedUrl))
                    {
                        ShowErrorMessage("Invalid Video URL!");
                    }
                    else if (lastUrl != cleanedUrl)
                    {
                        lastUrl = cleanedUrl;
                        // Use RequestSong to add a song to the queue, and initiate its download. It will start playing if the queue was empty before
                        photonView?.RPC(
                            "RequestSong",
                            RpcTarget.All,
                            cleanedUrl,
                            seconds,
                            PhotonNetwork.LocalPlayer.ActorNumber
                        );
                        GUI.FocusControl(null);
                    }
                }


                // Resume/Pause-Button
                string pauseButtonText = (boombox == null || boombox.audioSource == null || (boombox.audioSource?.clip == null && boombox.playbackQueue.Count == 0)) ? 
                    "..." : boombox.audioSource.isPlaying ? "\u258C\u258C PAUSE" : "\u25B6 RESUME"; // || PAUSE or > RESUME
                if (GUILayout.Button(pauseButtonText, buttonStyle, GUILayout.Height(40)))
                {
                    if (boombox != null && boombox.audioSource != null)
                    {
                        photonView?.RPC(
                            "PlayPausePlayback",
                            RpcTarget.All,
                            !boombox.isPlaying,
                            (long)(Boombox.GetCurrentTimeMilliseconds() - Math.Round(boombox.audioSource.time * 1000f)),
                            PhotonNetwork.LocalPlayer.ActorNumber
                        );
                    }
                }


                // Fast-Forward-Button (10 seconds forward)
                if (GUILayout.Button(">>", smallButtonStyle, GUILayout.Width(40), GUILayout.Height(40)))
                {
                    if (boombox != null && boombox.audioSource?.clip != null)
                    {
                        photonView?.RPC(
                            "SyncPlayback",
                            RpcTarget.All,
                            boombox.GetCurrentSongIndex(),
                            (long)(Boombox.GetCurrentTimeMilliseconds() - (Math.Round(boombox.audioSource.time * 1000f) + 10000)),
                            PhotonNetwork.LocalPlayer.ActorNumber
                        );
                    }
                }

                GUILayout.EndHorizontal();


                // Download status information for current song
                if (boombox != null && boombox.downloadHelper.IsProcessingQueue()
                    && boombox.currentSong?.GetAudioClip() == null && boombox.downloadHelper.GetCurrentDownloadUrl() != null
                )
                {
                    GUILayout.Space(10);
                    GUILayout.Label("Download in progress...", statusStyle);

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Force Cancel Download", buttonStyle, GUILayout.Width(200), GUILayout.Height(30)))
                    {
                        boombox.downloadHelper.ForceCancelDownload();
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }


                // Status Message Display
                GUILayout.Space(15);
                if (!string.IsNullOrEmpty(statusMessage))
                {
                    GUILayout.Label(statusMessage, statusStyle);
                    GUILayout.Space(5);
                }


                // Error Message Display
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    GUI.color = Color.red;
                    GUILayout.Label(errorMessage, labelStyle);
                    GUI.color = Color.white;
                    GUILayout.Space(5);
                }



                // Volume Control Section
                GUILayout.Space(15);
                float displayPercentage = normalizedVolume * 100f;
                GUILayout.Label($"Volume: {Mathf.Round(displayPercentage)}%", labelStyle);

                GUILayout.BeginHorizontal();

                    // check if the user started dragging the slider
                    if (Event.current.type == EventType.MouseDown &&
                        GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                    {
                        isVolumeSliderBeingDragged = true;
                    }

                    float newNormalizedVolume = GUILayout.HorizontalSlider(normalizedVolume, 0f, 1f, sliderStyle, GUI.skin.horizontalSliderThumb);

                    // if volume changed and we weren't already dragging, start tracking drag
                    if (newNormalizedVolume != normalizedVolume)
                    {
                        if (!isVolumeSliderBeingDragged) // Unnecessary check
                        {
                            isVolumeSliderBeingDragged = true;
                        }

                        // update local volume for immediate feedback while sliding
                        if (boombox != null && boombox.audioSource != null)
                        {
                            float actualVolume = normalizedVolume * boombox.maxVolumeLimit;
                            boombox.audioSource.volume = actualVolume;
                        }

                        normalizedVolume = newNormalizedVolume;
                    }

                GUILayout.EndHorizontal();


                // Quality Control Section
                GUILayout.Space(15);
                GUILayout.Label($"Audio Quality: {qualityLabels[qualityLevel]}", labelStyle);
                GUILayout.BeginHorizontal();

                    // check if the user started dragging the slider
                    if (Event.current.type == EventType.MouseDown &&
                        GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                    {
                        isQualitySliderBeingDragged = true;
                    }

                    float sliderValue = GUILayout.HorizontalSlider(qualityLevel, 0f, 4f, sliderStyle, GUI.skin.horizontalSliderThumb);
                    int newQualityLevel = Mathf.RoundToInt(sliderValue);

                    // if quality changed and we weren't already dragging, start tracking drag
                    if (newQualityLevel != qualityLevel && !isQualitySliderBeingDragged)
                    {
                        isQualitySliderBeingDragged = true;
                    }

                    qualityLevel = newQualityLevel;

                    // update local quality for immediate feedback while sliding
                    if (boombox != null)
                    {
                        boombox.SetQuality(qualityLevel);
                    }

                GUILayout.EndHorizontal();


                // Apply Quality Setting to Downloads Toggle
                GUILayout.Space(10);
                bool applyQualityToDownloads = Boombox.ApplyQualityToDownloads;
                bool newQualityDownloads = GUILayout.Toggle(applyQualityToDownloads, "Apply Quality Setting to Downloads");

                if (newQualityDownloads != applyQualityToDownloads)
                { 
                    Boombox.ApplyQualityToDownloads = newQualityDownloads;
                    // Logger.LogInfo($"Apply Quality Setting to Downloads: {newQualityDownloads}");
                }


                // Monsters Can Hear Music Toggle
                GUILayout.Space(10);
                bool monstersCanHear = Boombox.MonstersCanHearMusic;
                bool newMonstersCanHear = GUILayout.Toggle(monstersCanHear, "Monsters can hear audio");

                if (newMonstersCanHear != monstersCanHear)
                {
                    Boombox.MonstersCanHearMusic = newMonstersCanHear;
                    // Logger.LogInfo($"Monsters can hear audio: {newMonstersCanHear}");
                }

                // Loop Queue Toggle -- TODO: possibly only allow master client to set this
                GUILayout.Space(10);
                bool loop = boombox.LoopQueue;
                bool newLoop = GUILayout.Toggle(loop, "Loop queue");
                if (newLoop != loop && PhotonNetwork.IsMasterClient)
                {
                    photonView.RPC(
                        "UpdateLooping",
                        RpcTarget.All,
                        newLoop,
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                    /*
                    boombox.LoopQueue = newLoop;
                    // Logger.LogInfo($"Looping Queue: {newLoop}");
                    */
                }


                // Visual Effects Toggle
                GUILayout.Space(10);
                bool lightsOn = visualEffects != null && visualEffects.AreLightsOn();
                bool newLightsOn = GUILayout.Toggle(lightsOn, "RGB Lights enabled");
                if (newLightsOn != lightsOn && visualEffects != null)
                {
                    visualEffects.SetLights(newLightsOn);
                    // Logger.LogInfo($"Visual Effects Enabled: {lightsOn}");
                }


                // Visualizer Toggle
                GUILayout.Space(10);
                bool visualizerActive = visualizer != null;
                bool newVisualizerActive = GUILayout.Toggle(visualizerActive, "Audio Visualizer enabled");
                if (newVisualizerActive != visualizerActive)
                {
                    if (newVisualizerActive)
                    {
                        visualizer = gameObject.AddComponent<Visualizer>();
                        visualizer.audioSource = boombox.audioSource;
                        // Logger.LogInfo($"Visualizer Active: {visualizerActive}");
                    }
                    else
                    {
                        Destroy(visualizer);
                        visualizer = null;
                    }
                }

            // End Left Column
            GUILayout.EndScrollView();
        }


        // Draw the Queue Panel
        private void DrawQueue(Boombox boombox)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("🎶 Playback Queue", queueHeaderStyle);
            // Dismiss Queue
            if (GUILayout.Button("Dismiss Queue", smallButtonStyle))
            {
                photonView.RPC(
                    "DismissQueue",
                    (PhotonNetwork.IsMasterClient ? RpcTarget.All : RpcTarget.MasterClient),
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                lastUrl = null;
            }
            GUILayout.EndHorizontal();

            queueScrollPosition = GUILayout.BeginScrollView(queueScrollPosition, false, true, GUILayout.ExpandHeight(true));

                List<Boombox.AudioEntry> fullQueue = boombox.playbackQueue;
                int currentIndex = boombox.GetCurrentSongIndex();

                if (fullQueue.Count == 0) // && currentIndex == -1   Not necessary, may cause errors
                {
                    GUILayout.Label("Queue is empty and no song is playing.", labelStyle);
                }

                for (int i = 0; i < fullQueue.Count; i++)
                {
                    var entry = fullQueue[i];
                    bool isCurrent = (i == currentIndex);

                    GUIStyle styleToUse = isCurrent ? currentSongStyle : queueEntryStyle;

                    string prefix = isCurrent ? "\u25B6 " : $"{i - (currentIndex == -1 ? 0 : currentIndex)}. "; // Unicode triangle for current song, index for others
                    string displayText = ClipText(prefix + entry.Title, 280, styleToUse);

                    // Start Horizontal Block for Queue Item + Controls
                    GUILayout.BeginHorizontal(styleToUse, GUILayout.Height(32));

                        // 1. Song Title (takes up most of the space)
                        if (GUILayout.Button(displayText, styleToUse, GUILayout.ExpandWidth(true), GUILayout.Height(32)) &&! isCurrent)
                        {
                            photonView.RPC(
                                "SyncPlayback",
                                RpcTarget.All,
                                i,
                                Boombox.GetCurrentTimeMilliseconds(),
                                PhotonNetwork.LocalPlayer.ActorNumber
                            );
                        }

                        // Add Control Buttons for all items except the currently playing song
                        if (!isCurrent)
                        {
                            // 2. Move Up Button (Only if not the first in the upcoming queue)
                            if (i > 0) // Ensures you can't move the 2nd song into the 1st position if 1st is currently playing
                            {
                                if (GUILayout.Button("▲", smallButtonStyle, GUILayout.Width(25), GUILayout.Height(28)))
                                {
                                    // RPC Call to Boombox to move song up
                                    photonView.RPC(
                                        "MoveQueueItem",
                                        RpcTarget.All,
                                        i,
                                        i - 1,
                                        PhotonNetwork.LocalPlayer.ActorNumber
                                    );
                                }
                            }
                            else
                            {
                                // Placeholder for consistency
                                GUILayout.Space(25 + 5);
                            }

                            // 3. Move Down Button (Only if not the last item)
                            if (i + 1 < fullQueue.Count)
                            {
                                if (GUILayout.Button("▼", smallButtonStyle, GUILayout.Width(25), GUILayout.Height(28)))
                                {
                                    // RPC Call to Boombox to move song down
                                    photonView.RPC(
                                        "MoveQueueItem",
                                        RpcTarget.All,
                                        i,
                                        i + 1,
                                        PhotonNetwork.LocalPlayer.ActorNumber
                                    );
                                }
                            }
                            else
                            {
                                // Placeholder for consistency
                                GUILayout.Space(25 + 5);
                            }

                            // 4. Dismiss/Remove Button
                            if (GUILayout.Button("X", smallButtonStyle, GUILayout.Width(25), GUILayout.Height(28)))
                            {
                                // RPC Call to Boombox to remove song
                                photonView.RPC(
                                    "RemoveQueueItem",
                                    RpcTarget.All,
                                    i,
                                    PhotonNetwork.LocalPlayer.ActorNumber
                                );
                            }
                        }

                    // End Horizontal Block
                    GUILayout.EndHorizontal();
                }

            GUILayout.EndScrollView();
        }

        private string ClipText(string text, float maxWidth, GUIStyle style)
        {
            Vector2 size = style.CalcSize(new GUIContent(text));
            string result = text;
            int maxChars = text.Length; 
            while (size.x > maxWidth)
            {
                result = result.Substring(0, result.Length -4) + "...";
                size = style.CalcSize(new GUIContent(result));
            }
            return result;
        }

        private string PrintTime(float time)
        {
            return $"{(int)Math.Floor(time / 60)}:{(int)(time % 60)}";
        }

        private (string cleanedUrl, int seconds) IsValidVideoUrl(string url)
        {
            return DownloadHelper.IsValidVideoUrl(url);
        }

        private void ShowErrorMessage(string message)
        {
            Debug.LogError(message);
            errorMessage = message;
            errorMessageTime = Time.time + 3f;
        }

        private void OnDestroy()
        {
            showUI = false;

            if (backgroundTexture != null) Destroy(backgroundTexture);
            if (buttonTexture != null) Destroy(buttonTexture);
            if (sliderBackgroundTexture != null) Destroy(sliderBackgroundTexture);
            if (sliderThumbTexture != null) Destroy(sliderThumbTexture);
            if (textFieldBackgroundTexture != null) Destroy(textFieldBackgroundTexture);
        }

        // REMOVED AddToHistory - Moved to the Boombox class queue management.
    }
}