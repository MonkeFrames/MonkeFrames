using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using MonkeFrames.Editor.Utilities;
using MonkeFrames.Extensions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MonkeFrames.Editor.Windows;

/// <summary>Browse, inspect, install, and uninstall extensions published to the MonkeFrames marketplace.</summary>
public class ExtensionMarketplace : IEditorWindow
{
    private const string GalleryUrl = "https://extensions.monkeframes.com/api/gallery";
    private const string DetailUrl = "https://extensions.monkeframes.com/api/extension?id=";
    private const float RowHeight = 112f;

    public string Name => "Extension Marketplace";
    public Rect Rect => new Rect(Mathf.Max(12, (Screen.width - 900) / 2), Mathf.Max(36, (Screen.height - 680) / 2),
        Mathf.Min(900, Screen.width - 24), Mathf.Min(680, Screen.height - 50));

    private readonly List<JObject> _extensions = new();
    private readonly Dictionary<string, JObject> _details = new();
    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly HashSet<string> _textureLoads = new();
    private Vector2 _scroll;
    private Vector2 _detailScroll;
    private float _detailContentHeight = 1000f;
    private string _query = "";
    private string _error = "";
    private string _loadingId = "";
    private string _pendingInstallId = "";
    private string _selectedId = "";
    private string _status = "Loading marketplace...";
    private bool _loadingGallery;
    private bool _loadingDetail;
    private bool _busy;
    private bool? _verifiedFilter;
    private bool _oldestFirst;
    private int _imageIndex;

    public void OnOpen()
    {
        _selectedId = "";
        _details.Clear();
        _error = "";
        FetchGallery();
    }

    public void OnClose() { }

    private static string Field(JObject obj, params string[] names)
    {
        if (obj == null) return "";
        foreach (string name in names)
        {
            JToken token = obj.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token != null && token.Type != JTokenType.Null && token.Type != JTokenType.Object && token.Type != JTokenType.Array)
                return token.ToString();
        }
        return "";
    }

    private static bool? Verified(JObject obj)
    {
        string text = Field(obj, "publisherVerified", "verifiedPublisher", "verified", "isVerified", "publisher_verified");
        if (bool.TryParse(text, out bool result)) return result;
        string status = Field(obj, "publisherStatus", "verificationStatus", "publisher_status");
        if (status.Equals("verified", StringComparison.OrdinalIgnoreCase)) return true;
        if (status.Equals("unverified", StringComparison.OrdinalIgnoreCase)) return false;
        JToken publisher = obj?["publisher"];
        if (publisher is JObject publisherObject)
            return Verified(publisherObject);
        return null;
    }

    private static string Publisher(JObject obj)
    {
        JToken publisher = obj?["publisher"];
        return publisher is JObject publisherObject
            ? Field(publisherObject, "name", "displayName", "username", "id")
            : Field(obj, "publisher", "publisherName", "author", "developer", "creator");
    }

    private static string Id(JObject obj) => Field(obj, "id", "_id", "guid", "slug", "extensionId", "extension_id", "extensionID");
    private static string Title(JObject obj) => Field(obj, "name", "title", "displayName") is string n && n.Length > 0 ? n : Id(obj);
    private static string Version(JObject obj) => Field(obj, "version", "latestVersion", "latest_version", "currentVersion");
    private static string Summary(JObject obj) => Field(obj, "summary", "shortDescription", "short_description", "tagline", "description");
    private static string Updated(JObject obj) => Field(obj, "updatedAt", "updated_at", "updated", "updateDate", "update_date", "lastUpdated", "last_updated", "publishedAt", "published_at", "releaseDate");
    private static string IconUrl(JObject obj)
    {
        string icon = Field(obj, "icon", "iconUrl", "icon_url", "logo", "logoUrl", "thumbnail");
        if (!string.IsNullOrEmpty(icon)) return icon;
        JToken token = obj?.GetValue("icon", StringComparison.OrdinalIgnoreCase);
        return token is JObject iconObject ? Field(iconObject, "url", "src") : "";
    }

    private static string DownloadUrl(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (string name in new[] { "downloadUrl", "download_url", "downloadLink", "download_link", "download", "fileUrl", "file_url", "extensionUrl", "extension_url", "artifactUrl", "artifact_url" })
            {
                string value = Field(obj, name);
                if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return value;
            }
            foreach (JProperty property in obj.Properties())
            {
                string nested = DownloadUrl(property.Value);
                if (!string.IsNullOrEmpty(nested)) return nested;
            }
        }
        else if (token is JArray array)
            foreach (JToken item in array)
            {
                string nested = DownloadUrl(item);
                if (!string.IsNullOrEmpty(nested)) return nested;
            }
        return "";
    }

    private static bool TryDate(JObject obj, out DateTime date) => DateTime.TryParse(Updated(obj), out date);

    private static IEnumerable<JObject> GalleryItems(JToken token)
    {
        if (token is JArray array)
            return array.OfType<JObject>();
        if (token is JObject obj)
        {
            foreach (string key in new[] { "extensions", "gallery", "items", "results", "data" })
            {
                JToken nested = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
                if (nested is JArray list) return list.OfType<JObject>();
                if (nested is JObject nestedObj && nestedObj != obj)
                {
                    IEnumerable<JObject> found = GalleryItems(nestedObj);
                    if (found.Any()) return found;
                }
            }
        }
        return Enumerable.Empty<JObject>();
    }

    private void FetchGallery()
    {
        if (_loadingGallery) return;
        _loadingGallery = true;
        _error = "";
        _status = "Loading marketplace...";
        UIManager.Instance.StartCoroutine(RequestJson(GalleryUrl, token =>
        {
            _loadingGallery = false;
            _extensions.Clear();
            _extensions.AddRange(GalleryItems(token));
            _status = _extensions.Count == 0 ? "No extensions are published yet." : $"{_extensions.Count} extensions available";
            if (_extensions.Count == 0 && token == null) _error = "The marketplace response could not be read.";
        }, error => { _loadingGallery = false; _error = error; _status = "Could not load marketplace"; }));
    }

    private IEnumerator RequestJson(string url, Action<JToken> success, Action<string> failure)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 20;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                failure(request.responseCode == 404
                    ? "Not found (404). This extension may have been removed from the marketplace."
                    : request.error ?? "Request failed");
                yield break;
            }
            try { success(JToken.Parse(request.downloadHandler.text)); }
            catch (Exception ex) { failure($"Invalid marketplace response: {ex.Message}"); }
        }
    }

    private void Select(JObject item)
    {
        string requestId = Id(item);
        _selectedId = requestId;
        _imageIndex = 0;
        _detailScroll = Vector2.zero;
        if (_details.ContainsKey(requestId)) return;
        _loadingDetail = true;
        _loadingId = requestId;
        string escaped = UnityWebRequest.EscapeURL(requestId);
        UIManager.Instance.StartCoroutine(RequestJson(DetailUrl + escaped, token =>
        {
            _loadingDetail = false;
            JObject detail = UnwrapObject(token);
            if (detail == null) { _error = "Could not read extension details."; return; }
            _details[requestId] = detail;
            if (_pendingInstallId == requestId)
            {
                _pendingInstallId = "";
                JObject listing = _extensions.FirstOrDefault(x => Id(x) == requestId) ?? detail;
                Install(listing);
            }
        }, error => { _loadingDetail = false; _error = error; }));
    }

    private static JObject UnwrapObject(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (string key in new[] { "extension", "data", "result" })
                if (obj.GetValue(key, StringComparison.OrdinalIgnoreCase) is JObject nested) return nested;
            return obj;
        }
        return null;
    }

    private IEnumerable<JObject> VisibleExtensions()
    {
        IEnumerable<JObject> items = _extensions;
        if (_verifiedFilter.HasValue)
            items = items.Where(item => Verified(item) == _verifiedFilter);
        if (!string.IsNullOrWhiteSpace(_query))
            items = items.Where(item => (Title(item) + " " + Publisher(item) + " " + Summary(item) + " " + Field(item, "category") + " " + Id(item))
                .IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0);
        return _oldestFirst
            ? items.OrderBy(item => TryDate(item, out DateTime date) ? date : DateTime.MinValue).ToList()
            : items.OrderByDescending(item => TryDate(item, out DateTime date) ? date : DateTime.MinValue).ToList();
    }

    public void OnDraw()
    {
        Rect body = new Rect(12, 34, Rect.width - 24, Rect.height - 44);
        if (!string.IsNullOrEmpty(_selectedId)) DrawDetails(body);
        else DrawGallery(body);
    }

    private void DrawGallery(Rect body)
    {
        GUI.Label(new Rect(body.x, body.y, 220, 27), "Extension Marketplace", Theme.Title);
        if (GUI.Button(new Rect(body.xMax - 86, body.y + 1, 86, 26), "Refresh")) FetchGallery();

        _query = GUI.TextField(new Rect(body.x, body.y + 34, body.width * 0.42f, 30), _query);
        GUI.Label(new Rect(body.x + 8, body.y + 34, body.width * 0.42f - 16, 30),
            string.IsNullOrEmpty(_query) ? "Search extensions..." : "", Theme.Muted);

        int verified = _verifiedFilter == true ? 1 : _verifiedFilter == false ? 2 : 0;
        verified = Widgets.Segmented("market.verified", new Rect(body.x + body.width * 0.44f, body.y + 35, body.width * 0.34f, 28),
            verified, new[] { "All publishers", "Verified", "Unverified" });
        _verifiedFilter = verified == 1 ? true : verified == 2 ? false : (bool?)null;

        _oldestFirst = Widgets.Segmented("market.date", new Rect(body.xMax - body.width * 0.18f, body.y + 35, body.width * 0.18f, 28),
            _oldestFirst ? 1 : 0, new[] { "Updated", "Oldest" }) == 1;

        float listY = body.y + 72;
        if (!string.IsNullOrEmpty(_error))
            GUI.Label(new Rect(body.x, listY, body.width, 38), _error, Theme.MutedWrap);
        else if (_loadingGallery)
            GUI.Label(new Rect(body.x, listY, body.width, 30), "Connecting to extensions.monkeframes.com...", Theme.Muted);

        Rect view = new Rect(body.x, listY + 30, body.width, body.height - (listY - body.y) - 34);
        List<JObject> items = VisibleExtensions().ToList();
        float contentH = Math.Max(view.height, items.Count * RowHeight);
        _scroll = GUI.BeginScrollView(view, _scroll, new Rect(0, 0, view.width - 16, contentH));
        for (int i = 0; i < items.Count; i++) DrawCard(items[i], new Rect(0, i * RowHeight, view.width - 20, RowHeight - 7));
        GUI.EndScrollView();
        GUI.Label(new Rect(body.x, body.yMax - 24, body.width, 20), _status, Theme.MutedSmall);
    }

    private void DrawCard(JObject item, Rect r)
    {
        Theme.Fill(r, Theme.Surface, 8);
        Theme.Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), Theme.Field, 7);
        Rect icon = new Rect(r.x + 10, r.y + 12, 72, 72);
        DrawImage(IconUrl(item), icon, "MF");

        float textX = r.x + 94;
        GUI.Label(new Rect(textX, r.y + 8, r.width - 250, 23), Title(item), Theme.Title);
        string publisher = Publisher(item);
        GUI.Label(new Rect(textX, r.y + 31, r.width - 230, 20),
            string.IsNullOrEmpty(publisher) ? "Unknown publisher" : publisher, Theme.MutedSmall);
        bool? verified = Verified(item);
        Rect badge = new Rect(textX + Mathf.Min(170, (publisher.Length * 7) + 8), r.y + 32, verified == true ? 76 : 88, 18);
        Theme.Fill(badge, verified == true ? Theme.Accent.WithAlpha(0.20f) : new Color(1, 1, 1, 0.06f), 8);
        Theme.DrawText(badge, verified == true ? "✓ Verified" : "Unverified", Theme.LabelCenterSmall,
            verified == true ? Theme.Accent : Theme.TextMuted);

        GUI.Label(new Rect(textX, r.y + 52, r.width - 220, 19), $"Version {Version(item)}   ·   Updated {Updated(item)}", Theme.MutedSmall);
        GUI.Label(new Rect(textX, r.y + 73, r.width - 220, 30), Summary(item), Theme.MutedWrap);

        string id = Id(item);
        float buttonsX = r.xMax - 190;
        if (GUI.Button(new Rect(buttonsX, r.y + 19, 88, 30), "Details")) Select(item);
        DrawInstallButton(item, new Rect(buttonsX + 94, r.y + 19, 88, 30));
    }

    private void DrawInstallButton(JObject item, Rect rect)
    {
        if (_busy)
        {
            bool enabled = GUI.enabled;
            GUI.enabled = false;
            GUI.Button(rect, "Working...");
            GUI.enabled = enabled;
            return;
        }
        string path = InstallPath(Id(item));
        bool installed = File.Exists(path);
        if (GUI.Button(rect, installed ? "Uninstall" : "Install", installed ? Theme.DangerButton : Theme.AccentButton))
        {
            if (installed) Uninstall(item);
            else Install(item);
        }
    }

    private void DrawDetails(Rect body)
    {
        if (GUI.Button(new Rect(body.x, body.y, 86, 26), "‹ Gallery")) { _selectedId = ""; _error = ""; return; }
        JObject item = _extensions.FirstOrDefault(x => Id(x) == _selectedId);
        JObject detail = _details.TryGetValue(_selectedId, out JObject cached) ? cached : item;
        if (detail == null) { GUI.Label(new Rect(body.x, body.y + 38, body.width, 26), "Extension is unavailable.", Theme.Muted); return; }

        string title = Title(detail);
        GUI.Label(new Rect(body.x + 100, body.y, body.width - 200, 26), title, Theme.Title);
        DrawInstallButton(item ?? detail, new Rect(body.xMax - 100, body.y, 100, 27));
        if (_loadingDetail && _loadingId == _selectedId)
        {
            GUI.Label(new Rect(body.x, body.y + 42, body.width, 24), "Loading extension details...", Theme.Muted);
            return;
        }

        float messageHeight = string.IsNullOrEmpty(_error) ? 0 : 30;
        if (messageHeight > 0)
            GUI.Label(new Rect(body.x + 4, body.y + 30, body.width - 8, messageHeight), _error, Theme.MutedWrap);
        Rect view = new Rect(body.x, body.y + 36 + messageHeight, body.width, body.height - 38 - messageHeight);
        _detailScroll = GUI.BeginScrollView(view, _detailScroll, new Rect(0, 0, view.width - 18, _detailContentHeight));
        float y = 4;
        List<string> images = ImageUrls(detail);
        if (images.Count > 0)
        {
            Rect hero = new Rect(4, y, view.width - 40, 230);
            DrawImage(images[_imageIndex % images.Count], hero, "Preview unavailable");
            if (images.Count > 1)
            {
                if (GUI.Button(new Rect(hero.x + 8, hero.center.y - 16, 35, 32), "‹")) _imageIndex = (_imageIndex - 1 + images.Count) % images.Count;
                if (GUI.Button(new Rect(hero.xMax - 43, hero.center.y - 16, 35, 32), "›")) _imageIndex = (_imageIndex + 1) % images.Count;
                GUI.Label(new Rect(hero.x, hero.yMax - 24, hero.width, 20), $"{_imageIndex + 1} / {images.Count}", Theme.MutedCenter);
            }
            y += 240;
        }

        string description = Field(detail, "fullDescription", "full_description", "longDescription", "long_description", "readme", "description", "summary");
        GUI.Label(new Rect(5, y, view.width - 44, 20), "ABOUT", Theme.Header); y += 22;
        GUI.Label(new Rect(5, y, view.width - 44, 130), description, Theme.MutedWrap);
        y += Mathf.Max(120, Theme.MutedWrap.CalcHeight(new GUIContent(description), view.width - 44)) + 10;

        string categories = StringList(detail, "categories", "category", "tags");
        if (!string.IsNullOrEmpty(categories))
        {
            GUI.Label(new Rect(5, y, 120, 20), "CATEGORIES", Theme.Header);
            GUI.Label(new Rect(125, y, view.width - 164, 20), categories, Theme.Muted);
            y += 27;
        }

        GUI.Label(new Rect(5, y, view.width - 44, 20), "DETAILS", Theme.Header); y += 23;
        string publisher = Publisher(detail);
        DrawMetadata(ref y, view.width - 44, "Publisher", publisher);
        DrawMetadata(ref y, view.width - 44, "Verified publisher", Verified(detail) == true ? "Yes" : "No");
        DrawMetadata(ref y, view.width - 44, "Version", Version(detail));
        DrawMetadata(ref y, view.width - 44, "Updated", Updated(detail));
        DrawMetadata(ref y, view.width - 44, "Extension ID", _selectedId);

        foreach (JProperty prop in detail.Properties())
        {
            if (new[] { "id", "_id", "guid", "slug", "name", "title", "displayName", "publisher", "publisherName", "author", "version", "latestVersion", "summary", "description", "fullDescription", "full_description", "longDescription", "long_description", "readme", "icon", "iconUrl", "icon_url", "logo", "logoUrl", "thumbnail", "screenshots", "images", "thumbnails", "categories", "category", "tags", "updatedAt", "updated_at", "updated", "updateDate", "update_date", "lastUpdated", "last_updated", "publishedAt", "published_at", "releaseDate", "publisherVerified", "verifiedPublisher", "verified", "isVerified", "downloadUrl", "download_url", "fileUrl", "file_url", "url" }.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (prop.Value is JObject || prop.Value is JArray)
            {
                DrawNestedMetadata(ref y, view.width - 44, prop.Name, prop.Value, 0);
                continue;
            }
            DrawMetadata(ref y, view.width - 44, prop.Name, prop.Value.ToString());
        }
        _detailContentHeight = Mathf.Max(view.height, y + 12);
        GUI.EndScrollView();
    }

    private static void DrawMetadata(ref float y, float width, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        GUI.Label(new Rect(6, y, 150, 20), label, Theme.MutedSmall);
        GUI.Label(new Rect(156, y, width - 158, 20), value, Theme.Muted);
        y += 22;
    }

    private static void DrawNestedMetadata(ref float y, float width, string label, JToken value, int depth)
    {
        if (depth >= 2) return;
        if (value is JObject obj)
        {
            foreach (JProperty child in obj.Properties())
            {
                if (child.Value is JObject || child.Value is JArray)
                    DrawNestedMetadata(ref y, width, label + "." + child.Name, child.Value, depth + 1);
                else
                    DrawMetadata(ref y, width, label + "." + child.Name, child.Value.ToString());
            }
        }
        else if (value is JArray array)
        {
            string text = string.Join(", ", array.Where(item => item is not JObject && item is not JArray).Select(item => item.ToString()));
            DrawMetadata(ref y, width, label, text);
        }
    }

    private static string StringList(JObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JToken token = obj.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token is JArray array) return string.Join(", ", array.Select(x => x.ToString()));
            if (token != null && token.Type == JTokenType.String) return token.ToString();
        }
        return "";
    }

    private static List<string> ImageUrls(JObject obj)
    {
        var urls = new List<string>();
        foreach (string name in new[] { "thumbnails", "screenshots", "images", "gallery" })
        {
            JToken token = obj.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token is JArray arr)
                foreach (JToken entry in arr)
                {
                    string url = entry.Type == JTokenType.Object ? Field((JObject)entry, "url", "src", "image", "imageUrl", "image_url") : entry.ToString();
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) urls.Add(url);
                }
        }
        if (urls.Count == 0 && IconUrl(obj).StartsWith("http", StringComparison.OrdinalIgnoreCase)) urls.Add(IconUrl(obj));
        return urls.Distinct().ToList();
    }

    private void DrawImage(string url, Rect rect, string placeholder)
    {
        Theme.Fill(rect, Theme.Raised, 7);
        if (string.IsNullOrEmpty(url)) { Theme.DrawText(rect, placeholder, Theme.MutedCenter, Theme.TextMuted); return; }
        if (_textures.TryGetValue(url, out Texture2D texture) && texture != null)
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
        else
        {
            Theme.DrawText(rect, "Loading image...", Theme.MutedCenter, Theme.TextMuted);
            if (_textureLoads.Add(url)) UIManager.Instance.StartCoroutine(LoadImage(url));
        }
    }

    private IEnumerator LoadImage(string url)
    {
        using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
        {
            request.timeout = 20;
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
                _textures[url] = DownloadHandlerTexture.GetContent(request);
        }
        _textureLoads.Remove(url);
    }

    private static string InstallPath(string id)
    {
        string safe = string.Concat((id ?? "extension").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Constants.DataFolder, "extensions", safe + ".mfextension");
    }

    private void Install(JObject item)
    {
        if (_busy) return;
        JObject detail = _details.TryGetValue(Id(item), out JObject loaded) ? loaded : item;
        string download = DownloadUrl(detail);
        if (string.IsNullOrWhiteSpace(download))
        {
            string id = Id(item);
            if (!_details.ContainsKey(id))
            {
                _pendingInstallId = id;
                Select(item);
                return;
            }
            _error = "This listing does not include an extension download URL.";
            return;
        }
        _busy = true;
        UIManager.Instance.StartCoroutine(DownloadExtension(download, InstallPath(Id(item)), Title(detail)));
    }

    private IEnumerator DownloadExtension(string url, string path, string extensionName)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 60;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                _busy = false;
                _error = "Download failed: " + request.error;
                yield break;
            }
            byte[] bytes = request.downloadHandler.data;
            if (bytes == null || bytes.Length == 0)
            {
                _busy = false;
                _error = "The marketplace returned an empty extension file.";
                yield break;
            }
            string stagedPath = path + ".download";
            string installError = "";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(stagedPath, bytes);
                AssemblyName.GetAssemblyName(stagedPath);
                File.Copy(stagedPath, path, true);
                File.Delete(stagedPath);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch { }
                installError = ex.Message;
            }
            if (!string.IsNullOrEmpty(installError))
            {
                _busy = false;
                _error = "The downloaded file is not a valid MonkeFrames extension: " + installError;
                yield break;
            }
        }
        _busy = false;
        _error = "";
        UIManager.Instance.ReloadExtensions();
        UIManager.Instance.Status = $"Installed {extensionName}";
        FetchGallery();
    }

    private void Uninstall(JObject item)
    {
        if (_busy) return;
        string path = InstallPath(Id(item));
        if (!File.Exists(path)) { _error = "This extension was not installed through the marketplace."; return; }
        try { File.Delete(path); }
        catch (Exception ex) { _error = "Could not uninstall extension: " + ex.Message; return; }

        UIManager.Instance.ReloadExtensions();
        _error = "";
        UIManager.Instance.Status = $"Uninstalled {Title(item)}";
        FetchGallery();
    }
}
