using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Utilities;
using Photon.Voice.Unity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// Records every gorilla's movement and voice, then plays it back with script-free copies
/// ("puppets") that the Other Cameras and keyframes can film.
/// </summary>
[DefaultExecutionOrder(-40)]
public class ReplayManager : MonoBehaviour
{
    public static ReplayManager Instance;

    public static string Folder => SystemUtilities.Combine(Constants.DataFolder, "replays");
    public const string Extension = ".mfreplay";

    // ---------------- Settings ----------------
    public int RecordRate = 60;
    public bool RecordVoices = true;
    public bool RecordMyMic = true;
    public float MaxMinutes = 10f;

    public bool HideLive = true;
    public bool MuteGame = false;
    public bool Voice3D = true;
    public float VoiceVolume = 1f;
    public bool SyncWithKeyframes = true;
    public bool Loop = false;
    public float Speed = 1f;

    // ---------------- State ----------------
    public ReplayClip Clip { get; private set; }
    public bool Recording { get; private set; }
    public bool Viewing { get; private set; }
    public bool Playing;
    public double Time;
    public bool SyncDriving { get; private set; }

    public bool Busy => _busy;
    public string BusyText { get; private set; } = "";
    public readonly List<ReplayClip> Library = new();

    public float RecordedSeconds => Clip == null ? 0f : Clip.FrameCount / (float)Mathf.Max(1, Clip.Rate);
    public int RecordingPlayers => _bindings.Count(b => !b.Closed);
    public bool MicFound => _mic != null;

    // ---------------- Recording internals ----------------
    private sealed class Binding
    {
        public VRRig Rig;
        public string Key;
        public ReplayTrack Track;
        public Transform[] Parts;
        public VoiceTap Tap;
        public bool Closed;
        public float NextCosmeticCheck;
    }

    private readonly List<Binding> _bindings = new();
    private double _recStartReal, _recStartDsp;
    private int _recFrame;
    private float _nextScan;
    private float[] _audioTmp = new float[4096];
    private readonly HashSet<VRRig> _failedRigs = new();

    private AudioClip _mic;
    private string _micDevice;
    private bool _ownMic;
    private int _micSearches;
    private int _micPos = -1;
    private float _nextMicSearch;

    // ---------------- Viewing internals ----------------
    private readonly HashSet<Renderer> _hiddenLive = new();
    private float _nextHide;
    private float _savedListenerVolume = -1f;
    private int _posedFrame = -1;
    private int _lastHead = -1;
    private bool _audioPlaying;

    // ---------------- Background work ----------------
    private volatile bool _busy;
    private volatile string _pendingStatus;
    private ReplayClip _pendingLoad;
    private readonly object _pendingLock = new();

    public ReplayManager()
    {
        Instance = this;
    }

    private void Start()
    {
        try { Directory.CreateDirectory(Folder); } catch { }
        RefreshLibrary();

        // Fallback for setups where Application.onBeforeRender isn't delivered.
        if (GetComponent<ReplayManagerLate>() == null)
            gameObject.AddComponent<ReplayManagerLate>();
        if (GetComponent<ReplayStudio>() == null)
            gameObject.AddComponent<ReplayStudio>();
        if (GetComponent<SpectatorCams>() == null)
            gameObject.AddComponent<SpectatorCams>();
        if (GetComponent<PostFX>() == null)
            gameObject.AddComponent<PostFX>();
    }

    internal void LateFallback()
    {
        if (CameraModes.BeforeRenderWorks) return;
        if (Recording) Capture();
        ApplyPose();
    }

    private void OnEnable() => Application.onBeforeRender += OnBeforeRender;
    private void OnDisable() => Application.onBeforeRender -= OnBeforeRender;

    private void OnBeforeRender()
    {
        if (Recording)
            Capture();
        ApplyPose();
    }

    // =====================================================================
    //  Recording
    // =====================================================================

    public void StartRecording()
    {
        if (Recording || _busy) return;

        // Keep unsaved edits (trim, names, hidden gorillas) of the replay that's open.
        if (Clip != null && Clip.Dirty && Clip.FilePath != null)
            Save();

        if (Viewing) StopViewing();
        Unload();

        Clip = new ReplayClip
        {
            Name = "Replay " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"),
            Created = DateTime.Now,
            Rate = Mathf.Clamp(RecordRate, 10, 120),
        };

        _bindings.Clear();
        _failedRigs.Clear();
        _openCams.Clear();
        _recFrame = 0;
        _recStartReal = UnityEngine.Time.realtimeSinceStartupAsDouble;
        _recStartDsp = AudioSettings.dspTime;
        _nextScan = 0f;
        ResetMicClock();
        _nextMicSearch = 0f;
        _micSearches = 0;
        _mic = null;
        _ownMic = false;
        Recording = true;

        ScanForRecording();
        UIManager.Instance.Status = "Recording replay... everyone's movement" + (RecordVoices ? " and voices" : "") + ".";
    }

    public void ToggleRecording()
    {
        if (Recording) StopRecording();
        else StartRecording();
    }

    public void StopRecording()
    {
        if (!Recording) return;

        DrainAudio();
        foreach (Binding b in _bindings)
            Close(b);
        Recording = false;
        StopOwnMic();

        // Drop gorillas that were only there for an instant.
        for (int i = Clip.Tracks.Count - 1; i >= 0; i--)
        {
            ReplayTrack t = Clip.Tracks[i];
            t.TrimExcess();
            if (t.FrameCount < 2)
            {
                DestroyTrackObjects(t);
                Clip.Tracks.RemoveAt(i);
            }
        }
        _bindings.Clear();

        if (Clip.Tracks.Count == 0 || Clip.FrameCount < 2)
        {
            Unload();
            UIManager.Instance.Status = "Nothing was recorded.";
            return;
        }

        Clip.InPoint = 0f;
        Clip.OutPoint = 0f;
        Time = 0;
        Playing = false;
        StartViewing();

        UIManager.Instance.Status = $"Recorded {FormatTime(Clip.Length)} with {Clip.Tracks.Count} gorilla{(Clip.Tracks.Count == 1 ? "" : "s")}. Saving...";
        Save();
    }

    private void Capture()
    {
        double now = UnityEngine.Time.realtimeSinceStartupAsDouble - _recStartReal;
        int target = (int)(now * Clip.Rate);

        // Fill every frame up to now (duplicates if the game is running slower than the replay rate),
        // so every gorilla's frames stay lined up with the replay clock.
        bool cams = _recFrame <= target && SpectatorCams.Instance != null;
        if (cams)
            SpectatorCams.Instance.CollectForReplay(_camsNow);

        while (_recFrame <= target)
        {
            foreach (Binding b in _bindings)
            {
                if (b.Closed) continue;
                if (!Valid(b)) { Close(b); continue; }
                b.Track.AppendFrame(b.Rig.transform, b.Parts);
            }
            if (cams)
                CaptureCameras();
            _recFrame++;
        }
        Clip.FrameCount = _recFrame;
    }

    // ---- MonkeFrames spectator cameras (yours + other mod users') ----
    private readonly List<(string key, string name, bool local, Color color, Vector3 pos, Quaternion rot, float fov)> _camsNow = new();
    private readonly Dictionary<string, CamTrack> _openCams = new();
    private readonly List<string> _camScratch = new();

    private void CaptureCameras()
    {
        _camScratch.Clear();
        foreach (var c in _camsNow)
        {
            if (!_openCams.TryGetValue(c.key, out CamTrack t))
            {
                t = new CamTrack { Key = c.key, Name = c.name, IsLocal = c.local, Color = c.color, StartFrame = _recFrame };
                _openCams[c.key] = t;
                Clip.Cameras.Add(t);
            }
            t.Append(c.pos, c.rot, c.fov);
            _camScratch.Add(c.key);
        }

        // Cameras that stopped (player left, or went back to their normal view) end their track.
        if (_openCams.Count != _camScratch.Count)
        {
            var closed = new List<string>();
            foreach (string k in _openCams.Keys)
                if (!_camScratch.Contains(k)) closed.Add(k);
            foreach (string k in closed) _openCams.Remove(k);
        }
    }

    private void UpdateRecording()
    {
        if (UnityEngine.Time.unscaledTime >= _nextScan)
            ScanForRecording();

        // Pick up cosmetics that finish loading (or change) after a gorilla was first recorded.
        foreach (Binding b in _bindings)
        {
            if (b.Closed || UnityEngine.Time.unscaledTime < b.NextCosmeticCheck) continue;
            b.NextCosmeticCheck = UnityEngine.Time.unscaledTime + 2f;
            try { PuppetBuilder.AddNewRenderers(b.Track, b.Rig); }
            catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Replay] Cosmetic update failed: {ex.Message}"); }
        }

        DrainAudio();

        if (RecordedSeconds >= MaxMinutes * 60f)
        {
            UIManager.Instance.Status = $"Replay reached the {MaxMinutes:0} minute limit.";
            StopRecording();
        }
    }

    private void ScanForRecording()
    {
        _nextScan = UnityEngine.Time.unscaledTime + 0.5f;

        foreach (VRRig rig in LiveRigs())
        {
            if (_failedRigs.Contains(rig)) continue;

            string key = KeyOf(rig);
            Binding open = _bindings.FirstOrDefault(b => !b.Closed && b.Key == key);
            if (open != null)
            {
                if (open.Rig == rig)
                {
                    if (open.Track.Name == "Gorilla")
                        open.Track.Name = CameraModes.PlayerName(rig);
                    if (open.Tap == null)
                        AttachTap(open);   // their voice chat may connect a little after they appear
                    continue;
                }
                Close(open);
            }

            // The same rig now belongs to someone else: close the old player's track.
            foreach (Binding b in _bindings)
                if (!b.Closed && b.Rig == rig && b.Key != key)
                    Close(b);

            StartTrack(rig, key);
        }
    }

    private void StartTrack(VRRig rig, string key)
    {
        try
        {
            ReplayTrack track = new ReplayTrack
            {
                Key = key,
                Name = CameraModes.PlayerName(rig),
                IsLocal = rig.isOfflineVRRig,
                Color = rig.playerColor,
                StartFrame = _recFrame,
                AudioRate = AudioSettings.outputSampleRate,
            };

            PuppetBuilder.BuildFromLive(track, rig, out Transform[] parts);

            Binding b = new Binding { Rig = rig, Key = key, Track = track, Parts = parts };

            AttachTap(b);

            Clip.Tracks.Add(track);
            _bindings.Add(b);
        }
        catch (Exception ex)
        {
            _failedRigs.Add(rig);
            Console.WriteLine($"[MonkeFrames::Replay] Couldn't record {CameraModes.PlayerName(rig)}: {ex}");
        }
    }

    private void AttachTap(Binding b)
    {
        VRRig rig = b.Rig;
        if (!RecordVoices || rig == null || rig.isOfflineVRRig || rig.voiceAudio == null)
            return;

        VoiceTap tap = rig.voiceAudio.GetComponent<VoiceTap>();
        if (tap == null) tap = rig.voiceAudio.gameObject.AddComponent<VoiceTap>();
        tap.Clear();
        tap.Capturing = true;
        b.Tap = tap;
    }

    private bool Valid(Binding b) =>
        b.Rig != null && b.Rig.isActiveAndEnabled && KeyOf(b.Rig) == b.Key;

    private void Close(Binding b)
    {
        if (b.Closed) return;
        if (b.Tap != null)
        {
            DrainTap(b);
            b.Tap.Capturing = false;
        }
        b.Closed = true;
    }

    private void DrainAudio()
    {
        foreach (Binding b in _bindings)
            if (!b.Closed && b.Tap != null)
                DrainTap(b);

        if (RecordMyMic)
            ReadMic();
    }

    private void DrainTap(Binding b)
    {
        int n = b.Tap.Drain(ref _audioTmp, out double startDsp);
        if (n > 0)
            b.Track.AddAudio(_audioTmp, n, startDsp - _recStartDsp, AudioSettings.outputSampleRate);
    }

    // ---- Your own microphone (read from the game's voice-chat mic) ----

    private void ReadMic()
    {
        Binding local = _bindings.FirstOrDefault(b => !b.Closed && b.Track.IsLocal);
        if (local == null)
            return;

        // While we're using our own mic, keep looking for the game's (e.g. after joining a room).
        if ((_mic == null || _ownMic) && UnityEngine.Time.unscaledTime >= _nextMicSearch)
        {
            _nextMicSearch = UnityEngine.Time.unscaledTime + 3f;
            AudioClip ownClip = _ownMic ? _mic : null;
            string ownDevice = _micDevice;

            if (FindMic())
            {
                if (ownClip != null)
                    ReleaseOwnMic(ownClip, ownDevice, endDevice: ownDevice != _micDevice);
                ResetMicClock();
            }
            else if (ownClip != null)
            {
                _mic = ownClip;         // keep using ours
                _micDevice = ownDevice;
            }
            else if (++_micSearches >= 2)
            {
                // The game's voice chat isn't using a mic we can read (or you're not in a room).
                StartOwnMic();
                ResetMicClock();
            }
        }
        if (_mic == null)
            return;

        // The game restarted its mic (joined / left a room): find it again.
        bool recording;
        try { recording = Microphone.IsRecording(_micDevice); }
        catch { recording = false; }
        if (!recording)
        {
            _mic = null;
            _ownMic = false;
            _nextMicSearch = 0f;
            return;
        }

        int pos = Microphone.GetPosition(_micDevice);
        int total = _mic.samples;
        if (total <= 0) { _mic = null; return; }
        if (_micPos < 0) { _micPos = pos; return; }

        int n = (pos - _micPos + total) % total;
        if (n <= 0) return;

        int ch = Mathf.Max(1, _mic.channels);
        float[] buf = new float[n * ch];
        if (!_mic.GetData(buf, _micPos)) return;
        _micPos = pos;

        if (ch > 1)
            for (int i = 0; i < n; i++)
            {
                float s = 0f;
                for (int c = 0; c < ch; c++) s += buf[i * ch + c];
                buf[i] = s / ch;
            }

        // Timestamp by counting samples (no gaps / overlaps between reads); resync if the
        // mic clock drifts away from real time.
        int freq = Mathf.Max(1, _mic.frequency);
        double now = UnityEngine.Time.realtimeSinceStartupAsDouble - _recStartReal;
        double byClock = now - n / (double)freq;
        double t = _micAnchor + _micSamples / (double)freq;
        if (_micSamples < 0 || Math.Abs(t - byClock) > 0.25)
        {
            _micAnchor = byClock;
            _micSamples = 0;
            t = byClock;
        }
        _micSamples += n;

        local.Track.AddAudio(buf, n, t, freq);
    }

    private double _micAnchor;
    private long _micSamples = -1;

    private void ResetMicClock()
    {
        _micPos = -1;
        _micSamples = -1;
    }

    private void StartOwnMic()
    {
        try
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0)
                return;

            // Never take over a device someone else (the game) is already recording from.
            string device = Microphone.devices[0];
            if (Microphone.IsRecording(device) || Microphone.IsRecording(null))
                return;

            _micDevice = device;
            _mic = Microphone.Start(device, true, 2, 48000);
            _ownMic = _mic != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Couldn't open the microphone: {ex.Message}");
            _mic = null;
            _ownMic = false;
        }
    }

    private void StopOwnMic()
    {
        if (_ownMic && _mic != null)
            ReleaseOwnMic(_mic, _micDevice, endDevice: true);
        _ownMic = false;
        _mic = null;
    }

    private void ReleaseOwnMic(AudioClip clip, string device, bool endDevice)
    {
        try { if (endDevice) Microphone.End(device); } catch { }
        if (clip != null && clip != _mic) Destroy(clip);
    }

    private bool FindMic()
    {
        _mic = null;
        _ownMic = false;
        try
        {
            foreach (Recorder rec in FindObjectsByType<Recorder>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                object src = rec.GetType().GetProperty("InputSource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(rec);
                if (src is not MicWrapper mw || mw.Mic == null)
                    continue;

                _mic = mw.Mic;
                _micDevice = typeof(MicWrapper).GetField("device", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mw) as string;
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Mic lookup failed: {ex.Message}");
        }
        return false;
    }

    // =====================================================================
    //  Viewing / playback
    // =====================================================================

    public void StartViewing()
    {
        if (Clip == null || Recording || Clip.Tracks.Count == 0) return;

        EnsureVoices();
        Viewing = true;
        _posedFrame = -1;
        _lastHead = -1;
        if (Time < Clip.In || Time > Clip.Out) Time = Clip.In;
        _nextHide = 0f;
    }

    public void StopViewing()
    {
        if (!Viewing) return;
        Viewing = false;
        Playing = false;

        if (Clip != null)
        {
            foreach (ReplayTrack t in Clip.Tracks)
            {
                if (t.Puppet != null) t.Puppet.gameObject.SetActive(false);
                if (t.Voice != null) t.Voice.SetClock(Time, Speed, false);
            }
            foreach (CamTrack c in Clip.Cameras)
                if (c.Model != null) c.Model.SetActive(false);
        }

        RestoreLive();
        RestoreListener();

        CameraModes cm = CameraModes.Instance;
        if (cm != null && cm.Target is ReplayPuppet)
            cm.SetTarget(LiveSubject.For(CameraModes.LocalRig()), quiet: true);
    }

    public void TogglePlay()
    {
        if (Clip == null) return;
        if (!Viewing) StartViewing();
        if (!Playing && Time >= Clip.Out - 0.01) Time = Clip.In;
        Playing = !Playing;
    }

    public void Seek(double t)
    {
        if (Clip == null) return;
        Time = Math.Max(0, Math.Min(Clip.Length, t));
        _posedFrame = -1;   // let the gorillas be re-posed straight away
    }

    /// <summary>All gorillas in the replay (for the camera list).</summary>
    public IEnumerable<ReplayPuppet> Puppets()
    {
        if (Clip == null || !Viewing) yield break;
        foreach (ReplayTrack t in Clip.Tracks)
            if (t.Puppet != null && !t.Hidden)
                yield return t.Puppet;
    }

    private void UpdateViewing()
    {
        if (Clip == null) { Viewing = false; return; }

        float dt = UnityEngine.Time.unscaledDeltaTime;
        CameraManager cam = CameraManager.Instance;
        Windows.Player player = FindPlayerWindow();
        SyncDriving = false;
        _audioPlaying = false;

        if (SyncWithKeyframes && cam != null && cam.InPlayback)
        {
            // Project > Play / Export: follow the keyframe animation frame-for-frame.
            SyncDriving = true;
            Time = Clip.In + cam.AppliedPlaybackSeconds * Speed;
            _audioPlaying = !cam.doRecording;
        }
        else if (SyncWithKeyframes && player != null && (player.IsPlaying || (_lastHead >= 0 && player.HeadPosition != _lastHead)))
        {
            SyncDriving = true;
            float fps = Mathf.Max(1, KeyframeManager.Instance.Project.FPS);
            Time = Clip.In + player.HeadPosition / fps * Speed;
            _audioPlaying = player.IsPlaying;
            Playing = false;
        }
        else if (Playing)
        {
            Time += dt * Speed;
            if (Time >= Clip.Out)
            {
                if (Loop) Time = Clip.In;
                else { Time = Clip.Out; Playing = false; }
            }
            _audioPlaying = Playing;
        }

        if (player != null) _lastHead = player.HeadPosition;
        Time = Math.Max(0, Math.Min(Clip.Length, Time));

        UpdateVoices();

        if (HideLive)
        {
            if (UnityEngine.Time.unscaledTime >= _nextHide)
            {
                _nextHide = UnityEngine.Time.unscaledTime + 0.5f;
                HideLiveRigs();
            }
        }
        else if (_hiddenLive.Count > 0)
        {
            RestoreLive();
        }

        if (MuteGame)
        {
            if (_savedListenerVolume < 0f)
            {
                _savedListenerVolume = AudioListener.volume;
                AudioListener.volume = 0f;
            }
        }
        else
        {
            RestoreListener();
        }
    }

    private void UpdateVoices()
    {
        Camera cam = CameraManager.Instance != null ? CameraManager.Instance.Camera : null;

        foreach (ReplayTrack t in Clip.Tracks)
        {
            if (t.Voice == null) continue;

            bool audible = _audioPlaying && !t.VoiceMuted && !t.Hidden && Mathf.Abs(Speed) > 0.05f;
            t.Voice.SetClock(Time, Speed, audible);

            float vol = VoiceVolume * t.VoiceVolume;
            float pan = 0f;
            if (Voice3D && cam != null && t.Puppet != null)
            {
                Vector3 head = t.Puppet.Head.position;
                Vector3 d = head - cam.transform.position;
                float dist = d.magnitude;
                vol *= 1f / (1f + Mathf.Max(0f, dist - 1.5f) * 0.18f);
                if (dist > 0.01f)
                    pan = Mathf.Clamp(Vector3.Dot(cam.transform.right, d / dist), -1f, 1f) * 0.75f;
            }

            t.Voice.Gain = vol;
            t.Voice.Source.volume = 1f;
            t.Voice.Source.panStereo = pan;
        }
    }

    /// <summary>Pose every replay gorilla for the current replay time. Runs once per frame, before the camera is placed.</summary>
    public void ApplyPose()
    {
        if (_posedFrame == UnityEngine.Time.frameCount)
            return;
        _posedFrame = UnityEngine.Time.frameCount;

        if (!Viewing || Clip == null)
            return;

        float frame = (float)(Time * Clip.Rate);

        bool showCams = Classes.Settings.current?.ShowCamerasInReplays ?? true;
        foreach (CamTrack c in Clip.Cameras)
        {
            float lf = frame - c.StartFrame;
            bool vis = showCams && c.FrameCount > 0 && lf >= -0.5f && lf <= c.FrameCount - 0.5f;
            if (vis && c.Model == null)
                c.Model = CamModel.Create("Replay Camera " + c.Name, c.Color, c.IsLocal ? "Your camera" : c.Name);
            if (c.Model == null) continue;
            if (c.Model.activeSelf != vis) c.Model.SetActive(vis);
            if (!vis) continue;
            c.Sample(lf, out Vector3 cp, out Quaternion cq, out _);
            c.Model.transform.SetPositionAndRotation(cp, cq);
            CamModel.FaceLabel(c.Model);
        }
        foreach (ReplayTrack t in Clip.Tracks)
        {
            ReplayPuppet p = t.Puppet;
            if (p == null) continue;

            float local = frame - t.StartFrame;
            bool visible = !t.Hidden && local >= -0.5f && local <= t.FrameCount - 0.5f;

            if (p.gameObject.activeSelf != visible)
                p.gameObject.SetActive(visible);

            if (visible)
                t.Apply(local, p.transform, p.Parts);
        }
    }

    private void HideLiveRigs()
    {
        foreach (VRRig rig in FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (rig == null) continue;
            foreach (Renderer r in rig.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || r.forceRenderingOff) continue;
                r.forceRenderingOff = true;
                _hiddenLive.Add(r);
            }
        }
    }

    private void RestoreLive()
    {
        foreach (Renderer r in _hiddenLive)
            if (r != null)
                r.forceRenderingOff = false;
        _hiddenLive.Clear();
    }

    private void RestoreListener()
    {
        if (_savedListenerVolume >= 0f)
        {
            AudioListener.volume = _savedListenerVolume;
            _savedListenerVolume = -1f;
        }
    }

    private void EnsureVoices()
    {
        foreach (ReplayTrack t in Clip.Tracks)
            if (t.Voice == null && t.HasVoice)
                t.Voice = VoicePlayer.Create(t, PuppetBuilder.Container);
    }

    // =====================================================================
    //  Library: save / load / delete
    // =====================================================================

    private bool _saveQueued;

    public void Save()
    {
        if (Clip == null || Recording) return;
        if (_busy) { _saveQueued = true; return; }

        ReplayClip clip = Clip;
        string old = clip.FilePath;
        string path = UniquePath(clip.Name, old);

        clip.Dirty = false;          // edits made while saving mark it dirty again
        clip.FilePath = path;
        _saveQueued = false;
        _busy = true;
        BusyText = "Saving replay...";
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Folder);
                clip.Save(path);
                if (!string.IsNullOrEmpty(old) && !SamePath(old, path) && File.Exists(old))
                    File.Delete(old);
                _pendingStatus = $"Saved \"{clip.Name}\" ({FormatBytes(new FileInfo(path).Length)}).";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::Replay] Save failed: {ex}");
                _pendingStatus = "Couldn't save the replay: " + ex.Message;
                clip.Dirty = true;
                clip.FilePath = old;
            }
            finally
            {
                _busy = false;
            }
        });
    }

    /// <summary>File path for a name; never overwrites a different replay that already has that name.</summary>
    private static string UniquePath(string name, string current)
    {
        string baseName = SafeFileName(name);
        string path = Path.Combine(Folder, baseName + Extension);
        for (int i = 2; File.Exists(path) && !SamePath(path, current); i++)
            path = Path.Combine(Folder, $"{baseName} ({i}){Extension}");
        return path;
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public void Load(string path)
    {
        if (Recording || _busy) return;

        _busy = true;
        BusyText = "Loading replay...";
        Task.Run(() =>
        {
            try
            {
                ReplayClip clip = ReplayClip.Load(path);
                lock (_pendingLock) _pendingLoad = clip;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::Replay] Load failed: {ex}");
                _pendingStatus = "Couldn't load that replay: " + ex.Message;
                _busy = false;
            }
        });
    }

    private void FinishLoad(ReplayClip clip)
    {
        StartCoroutine(FinishLoadRoutine(clip));
    }

    private System.Collections.IEnumerator FinishLoadRoutine(ReplayClip clip)
    {
        bool loaded = false;
        try
        {
            if (Viewing) StopViewing();
            Unload();

            VRRig model = CameraModes.LocalRig();
            foreach (ReplayTrack t in clip.Tracks)
            {
                ReplayPuppet puppet = PuppetBuilder.BuildFromSaved(t, model);
                if (model != null)
                    yield return StartCoroutine(PuppetBuilder.LoadCosmetics(t, puppet, model));
            }

            Clip = clip;
            Time = clip.In;
            Playing = false;
            StartViewing();
            UIManager.Instance.Status = $"Loaded \"{clip.Name}\".";
            loaded = true;
        }
        finally
        {
            if (!loaded && Clip != clip)
                foreach (ReplayTrack t in clip.Tracks)
                    DestroyTrackObjects(t);
            _busy = false;
        }
    }

    public void Delete(ReplayClip entry)
    {
        try
        {
            if (entry?.FilePath != null && File.Exists(entry.FilePath))
                File.Delete(entry.FilePath);
            if (Clip != null && Clip.FilePath == entry?.FilePath)
                Clip.FilePath = null;
            UIManager.Instance.Status = $"Deleted \"{entry?.Name}\".";
        }
        catch (Exception ex)
        {
            UIManager.Instance.Status = "Couldn't delete: " + ex.Message;
        }
        RefreshLibrary();
    }

    public void RefreshLibrary()
    {
        Library.Clear();
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (string f in Directory.GetFiles(Folder, "*" + Extension))
            {
                try { Library.Add(ReplayClip.Load(f, headerOnly: true)); }
                catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Replay] Skipping {f}: {ex.Message}"); }
            }
            Library.Sort((a, b) => b.Created.CompareTo(a.Created));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Library scan failed: {ex.Message}");
        }
    }

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Folder}\"");
        }
        catch { }
    }

    /// <summary>Close the current replay and remove its gorillas.</summary>
    public void Unload()
    {
        if (Recording) return;
        if (Viewing) StopViewing();

        if (Clip != null)
        {
            foreach (ReplayTrack t in Clip.Tracks)
                DestroyTrackObjects(t);
            foreach (CamTrack c in Clip.Cameras)
                if (c.Model != null) { Destroy(c.Model); c.Model = null; }
        }
        Clip = null;
        Playing = false;
        Time = 0;
    }

    private static void DestroyTrackObjects(ReplayTrack t)
    {
        if (t.Puppet != null)
        {
            t.Puppet.ReleaseOwned();
            Destroy(t.Puppet.gameObject);
        }
        if (t.Voice != null) Destroy(t.Voice.gameObject);
        t.Puppet = null;
        t.Voice = null;
    }

    // =====================================================================
    //  Frame loop
    // =====================================================================

    private void Update()
    {
        string status = _pendingStatus;
        if (status != null)
        {
            _pendingStatus = null;
            UIManager.Instance.Status = status;
            RefreshLibrary();
        }

        ReplayClip loaded = null;
        lock (_pendingLock)
        {
            loaded = _pendingLoad;
            _pendingLoad = null;
        }
        if (loaded != null)
            FinishLoad(loaded);

        if (_saveQueued && !_busy)
            Save();

        // F9 starts / stops recording (not while typing in a text box).
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.f9Key.wasPressedThisFrame && GUIUtility.keyboardControl == 0
            && UIManager.Instance != null && UIManager.Instance.Drawing)
            ToggleRecording();

        if (Recording)
            UpdateRecording();
        else if (Viewing)
            UpdateViewing();
    }

    private void OnDestroy()
    {
        RestoreLive();
        RestoreListener();
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static IEnumerable<VRRig> LiveRigs()
    {
        VRRig local = CameraModes.LocalRig();
        if (local != null && local.isActiveAndEnabled)
            yield return local;

        foreach (VRRig rig in FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (rig == null || !rig.isActiveAndEnabled || rig.isOfflineVRRig)
                continue;

            NetPlayer owner = null;
            try { owner = rig.Creator; } catch { }
            if (owner == null || owner.IsLocal)
                continue;

            yield return rig;
        }
    }

    private static string KeyOf(VRRig rig)
    {
        if (rig.isOfflineVRRig) return "local";
        try
        {
            NetPlayer p = rig.Creator;
            if (p != null)
            {
                if (!string.IsNullOrEmpty(p.UserId)) return p.UserId;
                return "actor" + p.ActorNumber;
            }
        }
        catch { }
        return "rig" + rig.GetInstanceID();
    }

    private static Windows.Player FindPlayerWindow()
    {
        UIManager ui = UIManager.Instance;
        if (ui == null) return null;
        if (!ui.Drawing) return null;
        foreach (var w in ui.Windows)
            if (w.Visible && w.Window is Windows.Player p)
                return p;
        return null;
    }

    public static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Replay";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }

    public static string FormatTime(double s)
    {
        if (s < 0) s = 0;
        int m = (int)(s / 60);
        double sec = s - m * 60;
        return $"{m}:{sec:00.0}";
    }

    public static string FormatBytes(long b)
    {
        if (b >= 1024L * 1024 * 1024) return $"{b / (1024f * 1024f * 1024f):0.0} GB";
        if (b >= 1024L * 1024) return $"{b / (1024f * 1024f):0.0} MB";
        return $"{b / 1024f:0} KB";
    }
}

/// <summary>Records / poses late in the frame when Application.onBeforeRender isn't available.</summary>
[DefaultExecutionOrder(8900)]
public class ReplayManagerLate : MonoBehaviour
{
    private void LateUpdate() => ReplayManager.Instance?.LateFallback();
}
