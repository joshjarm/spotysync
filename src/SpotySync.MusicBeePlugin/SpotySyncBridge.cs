using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    internal sealed class SpotySyncBridge : IDisposable
    {
        public const int Port = 51983;
        private const string Prefix = "http://127.0.0.1:51983/mbsync/";
        private readonly Plugin.MusicBeeApiInterface api;
        private readonly Func<Control> invokerResolver;
        private readonly Action<string> externalLog;
        private readonly string dataDirectory;
        private readonly string tokenPath;
        private readonly string logPath;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        private readonly SemaphoreSlim operationLock = new SemaphoreSlim(1, 1);
        private HttpListener listener;
        private CancellationTokenSource cancellation;
        private string token;

        public SpotySyncBridge(Plugin.MusicBeeApiInterface api, Func<Control> invokerResolver, Action<string> log)
        {
            this.api = api;
            this.invokerResolver = invokerResolver;
            externalLog = log;
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBeeSyncToService");
            tokenPath = Path.Combine(dataDirectory, "AutomationToken.txt");
            logPath = Path.Combine(dataDirectory, "AutomationRunLog.txt");
        }

        public void Start()
        {
            Directory.CreateDirectory(dataDirectory);
            token = LoadOrCreateToken();
            listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            listener.Start();
            cancellation = new CancellationTokenSource();
            Task.Run(() => ListenAsync(cancellation.Token));
            Log("Portable bridge v21 listening on 127.0.0.1:" + Port + ".");
        }

        private string LoadOrCreateToken()
        {
            try
            {
                if (File.Exists(tokenPath))
                {
                    string existing = File.ReadAllText(tokenPath, Encoding.UTF8).Trim();
                    if (existing.Length >= 32) return existing;
                }
            }
            catch { }
            string created = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            File.WriteAllText(tokenPath, created, Encoding.UTF8);
            return created;
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && listener != null && listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleAsync(context), cancellationToken);
                }
                catch (HttpListenerException) { if (cancellationToken.IsCancellationRequested) return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex) { Log("Listener error: " + ex.Message); }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string path = (context.Request.Url.AbsolutePath ?? string.Empty).TrimEnd('/').ToLowerInvariant();
                if (context.Request.HttpMethod == "GET" && path.EndsWith("/health"))
                {
                    await ReplyAsync(context.Response, 200, new { ok = true, service = "SpotySync MusicBee Bridge", automationVersion = 21, tokenPath = tokenPath });
                    return;
                }
                if (!string.Equals(context.Request.Headers["X-MBSync-Token"], token, StringComparison.Ordinal))
                {
                    await ReplyAsync(context.Response, 401, new { ok = false, error = "Missing or invalid X-MBSync-Token header." });
                    return;
                }
                if (context.Request.HttpMethod != "POST")
                {
                    await ReplyAsync(context.Response, 404, new { ok = false, error = "Unknown endpoint." });
                    return;
                }
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8)) body = await reader.ReadToEndAsync();
                object result;
                if (path.EndsWith("/v2/snapshot")) result = Snapshot();
                else if (path.EndsWith("/v2/apply")) result = await ApplyAsync(json.Deserialize<ApplyRequest>(body) ?? new ApplyRequest());
                else if (path.EndsWith("/v2/import")) result = await ImportAsync(json.Deserialize<ImportRequest>(body) ?? new ImportRequest());
                else result = new { ok = false, error = "Unknown endpoint." };
                await ReplyAsync(context.Response, 200, result);
            }
            catch (Exception ex)
            {
                Log("Request failed: " + ex);
                try { await ReplyAsync(context.Response, 500, new { ok = false, error = ex.Message }); } catch { }
            }
        }

        private object Snapshot()
        {
            return Invoke(() =>
            {
                var tracks = new List<TrackDto>();
                string[] files = null;
                if (api.Library_QueryFilesEx("domain=library", out files) && files != null)
                {
                    foreach (string file in files.Where(path => !string.IsNullOrWhiteSpace(path))) tracks.Add(ReadTrack(file));
                }
                var playlists = new List<PlaylistDto>();
                if (api.Playlist_QueryPlaylists())
                {
                    string id;
                    while (!string.IsNullOrWhiteSpace(id = api.Playlist_QueryGetNextPlaylist()))
                    {
                        Plugin.PlaylistFormat type = api.Playlist_GetType(id);
                        if (type == Plugin.PlaylistFormat.Auto || type == Plugin.PlaylistFormat.Radio) continue;
                        string[] playlistFiles = null;
                        if (!api.Playlist_QueryFilesEx(id, out playlistFiles) || playlistFiles == null) playlistFiles = new string[0];
                        playlists.Add(new PlaylistDto { Id = id, Name = api.Playlist_GetName(id) ?? string.Empty, Files = playlistFiles.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).ToList() });
                    }
                }
                Log("Snapshot served: " + tracks.Count + " tracks, " + playlists.Count + " static playlists.");
                return new { ok = true, snapshot = new { libraryName = "MusicBee", tracks = tracks, playlists = playlists } };
            });
        }

        private async Task<object> ApplyAsync(ApplyRequest request)
        {
            if (!await operationLock.WaitAsync(0)) return new { ok = false, error = "Another MusicBee operation is already running." };
            try
            {
                if (string.IsNullOrWhiteSpace(request.PlaylistName)) return new { ok = false, error = "playlistName is required." };
                return Invoke(() => ApplyOnUi(request));
            }
            catch (Exception ex) { Log("Apply failed: " + ex); return new { ok = false, error = ex.Message }; }
            finally { operationLock.Release(); }
        }

        private object ApplyOnUi(ApplyRequest request)
        {
            var playlists = ReadPlaylistHeaders();
            string safeName = SafePlaylistName(request.PlaylistName);
            var match = playlists.FirstOrDefault(item => request.PlaylistId.Length > 0 && string.Equals(item.Id, request.PlaylistId, StringComparison.OrdinalIgnoreCase))
                ?? playlists.FirstOrDefault(item => string.Equals(item.Name, request.PlaylistName, StringComparison.OrdinalIgnoreCase))
                ?? playlists.FirstOrDefault(item => string.Equals(item.Name, safeName, StringComparison.OrdinalIgnoreCase));
            if (match != null && (api.Playlist_GetType(match.Id) == Plugin.PlaylistFormat.Auto || api.Playlist_GetType(match.Id) == Plugin.PlaylistFormat.Radio))
                return new { ok = false, error = "Smart/Radio playlists are read-only and cannot be synchronized." };
            string[] current = new string[0];
            if (match != null)
            {
                string[] queried = null;
                if (api.Playlist_QueryFilesEx(match.Id, out queried) && queried != null) current = queried.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            string digest = Digest(current);
            if (match != null && request.ExpectedDigest.Length > 0 && !string.Equals(request.ExpectedDigest, digest, StringComparison.OrdinalIgnoreCase))
                return new { ok = false, error = "MusicBee playlist changed after preview; refresh the preview before applying." };
            string[] requested = (request.DesiredFiles ?? new List<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] desired = request.AllowRemovals ? requested : current.Concat(requested).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            int added = desired.Except(current, StringComparer.OrdinalIgnoreCase).Count();
            int removed = current.Except(desired, StringComparer.OrdinalIgnoreCase).Count();
            if (removed > Math.Max(0, request.MaxRemovals)) return new { ok = false, error = "MusicBee removal safety cap exceeded (" + removed + " > " + request.MaxRemovals + ")." };
            if (request.DryRun || added + removed == 0) return new { ok = true, added = added, removed = removed, total = desired.Length, dryRun = request.DryRun };
            if (removed > 0) Backup(request.PlaylistName, current);
            string id = match == null ? api.Playlist_CreatePlaylist(string.Empty, safeName, desired) : match.Id;
            bool saved = match == null ? !string.IsNullOrWhiteSpace(id) : api.Playlist_SetFiles(match.Id, desired);
            if (!saved) return new { ok = false, error = "MusicBee refused to create or update the playlist." };
            Log("Applied '" + request.PlaylistName + "': +" + added + "/-" + removed + ", total " + desired.Length + ".");
            return new { ok = true, playlistId = id, added = added, removed = removed, total = desired.Length };
        }

        /// <summary>
        /// MusicBee stores each playlist as a file, so names such as ":(", "5-9-26 &lt;/3" or
        /// "chill/cozy" were refused. Swap Windows-reserved characters for look-alikes so the
        /// playlist still reads the same; SpotySync matches names ignoring punctuation.
        /// </summary>
        private static string SafePlaylistName(string name)
        {
            var builder = new StringBuilder((name ?? string.Empty).Length);
            foreach (char c in name ?? string.Empty)
            {
                switch (c)
                {
                    case ':': builder.Append('꞉'); break;  // ꞉
                    case '<': builder.Append('‹'); break;  // ‹
                    case '>': builder.Append('›'); break;  // ›
                    case '/': builder.Append('∕'); break;  // ∕
                    case '\\': builder.Append('⧵'); break; // ⧵
                    case '?': builder.Append('？'); break;  // ？
                    case '*': builder.Append('＊'); break;  // ＊
                    case '"': builder.Append('”'); break;  // ”
                    case '|': builder.Append('｜'); break;  // ｜
                    default: builder.Append(c < ' ' ? ' ' : c); break;
                }
            }
            string safe = builder.ToString().Trim().TrimEnd('.').Trim();
            return safe.Length == 0 ? "Playlist" : safe;
        }

        private async Task<object> ImportAsync(ImportRequest request)
        {
            if (!await operationLock.WaitAsync(0)) return new { ok = false, error = "Another MusicBee operation is already running." };
            try
            {
                // Preserve every requested path even when MusicBee's folder
                // monitor has already renamed it. The old File.Exists filter
                // caused a two-track batch to become 1/1 before import began.
                string[] files = (request.Files ?? new List<string>()).Where(file => !string.IsNullOrWhiteSpace(file))
                    .Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var libraryBeforeImport = Invoke(QueryLibraryFiles);
                var returnedPaths = Invoke(() =>
                {
                    var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in files)
                    {
                        if (!File.Exists(file))
                        {
                            string relocated = FindRelocatedLibraryFile(file, libraryBeforeImport);
                            if (!string.IsNullOrWhiteSpace(relocated))
                            {
                                results[file] = relocated;
                                Log("MusicBee reconciled renamed import path: " + file + " -> " + relocated);
                            }
                            else
                            {
                                results[file] = string.Empty;
                                Log("MusicBee import path moved before handoff; checking the refreshed library: " + file);
                            }
                            continue;
                        }
                        if (libraryBeforeImport.Contains(file))
                        {
                            results[file] = file;
                            continue;
                        }
                        string result = api.Library_AddFileToLibrary(file, Plugin.LibraryCategory.Music) ?? string.Empty;
                        results[file] = string.IsNullOrWhiteSpace(result) ? string.Empty : NormalizePath(result);
                        Log("MusicBee import requested: " + file + (result.Length > 0 ? " -> " + result : " (no path returned)"));
                    }
                    api.MB_RefreshPanels();
                    return results;
                });
                await Task.Delay(750);

                var libraryFiles = Invoke(QueryLibraryFiles);
                var recognized = ResolveImportedFiles(files, returnedPaths, libraryFiles);
                var missing = files.Where(file => !recognized.ContainsKey(file)).ToArray();
                if (missing.Length > 0)
                {
                    Log("MusicBee did not expose " + missing.Length + " imported file(s) yet; retrying once.");
                    Invoke(() =>
                    {
                        foreach (string file in missing)
                        {
                            string result = api.Library_AddFileToLibrary(file, Plugin.LibraryCategory.Music) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(result)) returnedPaths[file] = NormalizePath(result);
                        }
                        api.MB_RefreshPanels();
                        return true;
                    });
                    await Task.Delay(1000);
                    libraryFiles = Invoke(QueryLibraryFiles);
                    recognized = ResolveImportedFiles(files, returnedPaths, libraryFiles);
                }

                int primed = 0;
                string primeWarning = string.Empty;
                if (request.PrimeAfterImport && recognized.Count > 0)
                {
                    try
                    {
                        int delay = Math.Max(250, Math.Min(5000, request.PrimeMilliseconds));
                        primed = await PrimeTracksAsync(recognized.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), delay);
                        if (primed != recognized.Count) primeWarning = "MusicBee silently initialized " + primed + " of " + recognized.Count + " imported file(s).";
                    }
                    catch (Exception ex)
                    {
                        primeWarning = ex.Message;
                        Log("Silent MusicBee initialization failed without invalidating the import: " + ex);
                    }
                }

                foreach (string file in files.Where(file => !recognized.ContainsKey(file)))
                    Log("WARNING: MusicBee still does not report the imported file: " + file);
                Log("MusicBee import verified " + recognized.Count + "/" + files.Length + " file(s); silently initialized " + primed + ".");
                return new { ok = true, requested = files.Length, imported = recognized.Count, primed = primed, primeWarning = primeWarning };
            }
            catch (Exception ex) { return new { ok = false, error = ex.Message }; }
            finally { operationLock.Release(); }
        }

        private HashSet<string> QueryLibraryFiles()
        {
            string[] files = null;
            if (!api.Library_QueryFilesEx("domain=library", out files) || files == null)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(files.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> ResolveImportedFiles(IEnumerable<string> requested, IDictionary<string, string> returned, ISet<string> library)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string original in requested)
            {
                string imported;
                if (returned.TryGetValue(original, out imported) && !string.IsNullOrWhiteSpace(imported) && library.Contains(imported))
                    result[original] = imported;
                else if (library.Contains(original))
                    result[original] = original;
                else
                {
                    string relocated = FindRelocatedLibraryFile(original, library);
                    if (!string.IsNullOrWhiteSpace(relocated)) result[original] = relocated;
                }
            }
            return result;
        }

        private static string FindRelocatedLibraryFile(string requested, IEnumerable<string> library)
        {
            if (string.IsNullOrWhiteSpace(requested)) return string.Empty;
            string normalizedRequested = NormalizePath(requested);
            string directory = Path.GetDirectoryName(normalizedRequested) ?? string.Empty;
            string extension = Path.GetExtension(normalizedRequested);
            string stem = FileNameIdentity(Path.GetFileNameWithoutExtension(normalizedRequested));
            if (stem.Length < 3) return string.Empty;

            var candidates = library.Where(candidate =>
            {
                if (string.IsNullOrWhiteSpace(candidate)) return false;
                string normalizedCandidate = NormalizePath(candidate);
                if (!string.Equals(Path.GetDirectoryName(normalizedCandidate) ?? string.Empty, directory, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(Path.GetExtension(normalizedCandidate), extension, StringComparison.OrdinalIgnoreCase)) return false;
                string candidateStem = FileNameIdentity(Path.GetFileNameWithoutExtension(normalizedCandidate));
                return candidateStem.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                       (candidateStem.Length > stem.Length && candidateStem.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            return candidates.Length == 1 ? NormalizePath(candidates[0]) : string.Empty;
        }

        private static string FileNameIdentity(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private async Task<int> PrimeTracksAsync(IList<string> files, int delayMilliseconds)
        {
            string previousFile = Invoke(() => api.NowPlaying_GetFileUrl()) ?? string.Empty;
            int previousPosition = Invoke(() => api.Player_GetPosition());
            Plugin.PlayState previousState = Invoke(() => api.Player_GetPlayState());
            bool previousMute = Invoke(() => api.Player_GetMute());
            bool previousScrobble = Invoke(() => api.Player_GetScrobbleEnabled());
            int primed = 0;
            try
            {
                Invoke(() => api.Player_SetMute(true));
                Invoke(() => api.Player_SetScrobbleEnabled(false));
                for (int index = 0; index < files.Count; index++)
                {
                    string file = files[index];
                    string display = Invoke(() =>
                    {
                        string artist = api.Library_GetFileTag(file, Plugin.MetaDataType.Artist) ?? string.Empty;
                        string title = api.Library_GetFileTag(file, Plugin.MetaDataType.TrackTitle) ?? string.Empty;
                        return string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)
                            ? Path.GetFileNameWithoutExtension(file)
                            : (artist + " - " + title).Trim(' ', '-');
                    });
                    Log("Silently initializing track " + (index + 1) + "/" + files.Count + ": " + display);
                    bool started = Invoke(() => api.NowPlayingList_PlayNow(file));
                    started = started && await WaitUntilNowPlayingAsync(file, 4000);
                    if (!started)
                    {
                        Log("WARNING: MusicBee could not activate the imported file for silent initialization: " + file);
                        continue;
                    }
                    if (Invoke(() => api.Player_GetPlayState()) != Plugin.PlayState.Playing) Invoke(() => api.Player_PlayPause());
                    await Task.Delay(delayMilliseconds);
                    primed++;
                }
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(previousFile))
                    {
                        Invoke(() => api.NowPlayingList_PlayNow(previousFile));
                        await WaitUntilNowPlayingAsync(previousFile, 3000);
                        Invoke(() => api.Player_SetPosition(previousPosition));
                        var currentState = Invoke(() => api.Player_GetPlayState());
                        if (previousState == Plugin.PlayState.Paused && currentState == Plugin.PlayState.Playing) Invoke(() => api.Player_PlayPause());
                        else if (previousState == Plugin.PlayState.Stopped) Invoke(() => api.Player_Stop());
                    }
                    else Invoke(() => api.Player_Stop());
                }
                catch (Exception ex) { Log("Could not completely restore the previous MusicBee playback state: " + ex.Message); }
                try { Invoke(() => api.Player_SetScrobbleEnabled(previousScrobble)); } catch { }
                try { Invoke(() => api.Player_SetMute(previousMute)); } catch { }
            }
            return primed;
        }

        private async Task<bool> WaitUntilNowPlayingAsync(string expectedFile, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                string actual = Invoke(() => api.NowPlaying_GetFileUrl()) ?? string.Empty;
                if (PathsEqual(actual, expectedFile)) return true;
                await Task.Delay(75);
            }
            return false;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try { return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase); }
        }

        private TrackDto ReadTrack(string file)
        {
            var info = new FileInfo(file);
            return new TrackDto
            {
                Path = NormalizePath(file), Title = api.Library_GetFileTag(file, Plugin.MetaDataType.TrackTitle) ?? string.Empty,
                Artist = api.Library_GetFileTag(file, Plugin.MetaDataType.Artist) ?? string.Empty,
                Album = api.Library_GetFileTag(file, Plugin.MetaDataType.Album) ?? string.Empty,
                Comment = api.Library_GetFileTag(file, Plugin.MetaDataType.Comment) ?? string.Empty,
                DurationMs = ParseDuration(api.Library_GetFileProperty(file, Plugin.FilePropertyType.Duration)),
                Size = info.Exists ? info.Length : 0, ModifiedUtcTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0
            };
        }

        private List<PlaylistHeader> ReadPlaylistHeaders()
        {
            var result = new List<PlaylistHeader>();
            if (!api.Playlist_QueryPlaylists()) return result;
            string id;
            while (!string.IsNullOrWhiteSpace(id = api.Playlist_QueryGetNextPlaylist())) result.Add(new PlaylistHeader { Id = id, Name = api.Playlist_GetName(id) ?? string.Empty });
            return result;
        }

        private T Invoke<T>(Func<T> action)
        {
            Control control = invokerResolver();
            if (control == null || control.IsDisposed) throw new InvalidOperationException("MusicBee UI dispatcher is not available.");
            if (!control.InvokeRequired) return action();
            T result = default(T);
            Exception captured = null;
            using (var complete = new ManualResetEventSlim(false))
            {
                control.BeginInvoke(new Action(() => { try { result = action(); } catch (Exception ex) { captured = ex; } finally { complete.Set(); } }));
                if (!complete.Wait(TimeSpan.FromSeconds(45))) throw new TimeoutException("Timed out waiting for MusicBee's UI thread. Close any modal MusicBee dialog and retry.");
            }
            if (captured != null) throw new InvalidOperationException("MusicBee API operation failed.", captured);
            return result;
        }

        private async Task ReplyAsync(HttpListenerResponse response, int status, object payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(payload));
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }

        private void Backup(string name, IEnumerable<string> files)
        {
            string safe = string.Join("_", (name ?? "Playlist").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string folder = Path.Combine(dataDirectory, "PlaylistBackups");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + safe + ".m3u8");
            File.WriteAllLines(path, new[] { "#EXTM3U" }.Concat(files), new UTF8Encoding(false));
            Log("Safety backup: " + path);
        }

        private static string Digest(IEnumerable<string> files)
        {
            string joined = string.Join("\n", files.Select(path => NormalizePath(path).ToLowerInvariant()));
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
                var result = new StringBuilder(24);
                for (int i = 0; i < 12; i++) result.Append(hash[i].ToString("x2"));
                return result.ToString();
            }
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return (path ?? string.Empty).Trim(); }
        }

        private static int ParseDuration(string value)
        {
            int number;
            if (int.TryParse(value, out number)) return number;
            TimeSpan time;
            return TimeSpan.TryParse(value, out time) ? (int)Math.Min(int.MaxValue, time.TotalMilliseconds) : 0;
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + message;
            try { Directory.CreateDirectory(dataDirectory); File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
            try { if (externalLog != null) externalLog(message); } catch { }
        }

        public void Dispose()
        {
            try { if (cancellation != null) cancellation.Cancel(); } catch { }
            try { if (listener != null) listener.Stop(); } catch { }
            try { if (listener != null) listener.Close(); } catch { }
            operationLock.Dispose();
        }

        private sealed class ApplyRequest
        {
            public string PlaylistId { get; set; } = string.Empty;
            public string PlaylistName { get; set; } = string.Empty;
            public List<string> DesiredFiles { get; set; } = new List<string>();
            public string ExpectedDigest { get; set; } = string.Empty;
            public bool AllowRemovals { get; set; }
            public int MaxRemovals { get; set; }
            public bool DryRun { get; set; }
        }
        private sealed class ImportRequest
        {
            public List<string> Files { get; set; } = new List<string>();
            public bool PrimeAfterImport { get; set; }
            public int PrimeMilliseconds { get; set; } = 1000;
        }
        private sealed class PlaylistHeader { public string Id { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; }
        private sealed class PlaylistDto { public string Id { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public List<string> Files { get; set; } = new List<string>(); }
        private sealed class TrackDto
        {
            public string Path { get; set; } = string.Empty; public string Title { get; set; } = string.Empty; public string Artist { get; set; } = string.Empty;
            public string Album { get; set; } = string.Empty; public string Comment { get; set; } = string.Empty; public int DurationMs { get; set; }
            public long Size { get; set; } public long ModifiedUtcTicks { get; set; }
        }
    }
}
