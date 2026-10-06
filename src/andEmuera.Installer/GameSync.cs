using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AndEmuera.Installer;

sealed record LocalFile(string Rel, long Size, long Time, string FullPath);

/// <summary>何を送るか。<see cref="GameSync.PlanAsync"/> が作る。</summary>
sealed class SyncPlan
{
	public required string Name { get; init; }
	public required string LocalRoot { get; init; }
	public required bool ExistsOnDevice { get; init; }
	public required int LocalCount { get; init; }
	public required long LocalBytes { get; init; }
	public List<LocalFile> Send { get; } = [];
	public int Added { get; set; }
	public int Changed { get; set; }
	public int SkippedSave { get; set; }
	public int OnlyRemote { get; set; }
	/// <summary>端末にだけある ERB/CSV (端末の今の綴りの相対パス)。送る前に消す。</summary>
	public List<string> Delete { get; } = [];
	/// <summary>端末側の名前を PC の大文字小文字に合わせる改名 (浅い順)。</summary>
	public List<(string Old, string New)> Renames { get; } = [];
	public bool BackupSave { get; set; }
	public long SendBytes => Send.Sum(f => f.Size);
	public bool UpToDate => Send.Count == 0 && Renames.Count == 0 && Delete.Count == 0;
}

readonly record struct SyncProgress(long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles, string Message);

/// <summary>
/// ゲームフォルダを端末へ送る。tools/deploy.ps1 と同じ考え方:
///
/// <list type="bullet">
/// <item>端末に無ければ全部、あれば差分だけ (サイズが違うか、PC 側の更新時刻のほうが新しいもの)。PC 側を正とする</item>
/// <item>大文字小文字だけ違う名前は、端末側を PC の綴りに改名する</item>
/// <item>sav/ は、端末にすでにあるゲームには送らない (端末で進めたセーブを守る)。送るときは先に控える</item>
/// <item>ERB/ と CSV/ の下は PC と同じ中身にそろえる (端末にだけあるファイルは消す)。
/// Emuera はこの下を全部読むので、PC で消したり名前を変えたりした ERB が端末に残ると、
/// 関数の二重定義や古い定義の読み込みで動きがおかしくなる</item>
/// <item>それ以外の場所で端末にだけあるファイルは消さない (端末のセーブや、端末にだけ置いた画像など)</item>
/// </list>
///
/// adb の罠も同じように避ける。remote 側のディレクトリ名が日本語だと adb push がハングし、
/// 複数ファイルを 1 回の push でディレクトリへ送ると成功表示のまま何も書かれないので、
/// 送るものはローカルの一時フォルダに構造ごと並べ、ASCII 名のディレクトリ 1 個として送ってから端末上で合流させる。
/// </summary>
static partial class GameSync
{
	const string StageRemote = Device.GamesDir + "/" + Device.StagePrefix + "stage";

	/// <summary>1 回の push にまとめる上限。進捗と中止の細かさを決める。</summary>
	const long BatchBytes = 96L * 1024 * 1024;
	const int BatchFiles = 4000;

	static readonly Regex SavePattern = SaveRegex();

	/// <summary>PC と同じ中身にそろえる (端末にだけあるファイルを消す) 場所。</summary>
	static readonly Regex MirrorPattern = MirrorRegex();

	public static bool IsGameFolder(string dir) =>
		// Windows は大文字小文字を区別しないので CSV / csv のどちらでも当たる
		Directory.Exists(Path.Combine(dir, "csv")) && Directory.Exists(Path.Combine(dir, "erb"));

	/// <summary>ゲームフォルダの名前として端末に置けるか。</summary>
	public static string? ValidateName(string name)
	{
		if (name.StartsWith(Device.StagePrefix, StringComparison.OrdinalIgnoreCase))
			return $"「{Device.StagePrefix}」で始まるフォルダ名は使えません。";
		if (name.Contains('\n') || name.Contains('/'))
			return "フォルダ名に使えない文字があります。";
		return null;
	}

	/// <summary>
	/// PC 側の一覧。DirectoryInfo.EnumerateFiles は FindFirstFile の結果からサイズと時刻を埋めるので、
	/// ファイルごとの stat が要らない (HDD 上の 18 万ファイルで数十秒)。
	/// </summary>
	static Dictionary<string, LocalFile> LocalIndex(string root, CancellationToken ct)
	{
		var index = new Dictionary<string, LocalFile>(StringComparer.Ordinal);
		var info = new DirectoryInfo(root);
		int prefix = info.FullName.TrimEnd('\\').Length + 1;
		var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
		foreach (var f in info.EnumerateFiles("*", options))
		{
			ct.ThrowIfCancellationRequested();
			string rel = f.FullName[prefix..].Replace('\\', '/');
			index[rel] = new LocalFile(rel, f.Length, new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds(), f.FullName);
		}
		return index;
	}

	/// <summary>
	/// 端末側の一覧。toybox find の -printf で「サイズ 時刻 相対パス」を出す (%T@ は小数付きの UNIX 秒)。
	/// -exec stat {} + は日本語の長いパスが続くと Argument list too long で落ちるので使わない。
	/// </summary>
	static async Task<Dictionary<string, (long Size, long Time)>> RemoteIndexAsync(Adb adb, string remoteDir, CancellationToken ct)
	{
		string output = await adb.ScriptAsync($"cd {Adb.Q(remoteDir)} && find . -type f -printf '%s %T@ %P\\n'", ct);
		var index = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
		foreach (string line in output.Split('\n'))
		{
			var m = RemoteLineRegex().Match(line);
			if (m.Success) index[m.Groups[3].Value] = (long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
		}
		return index;
	}

	static async Task<bool> RemoteDirExistsAsync(Adb adb, string path, CancellationToken ct) =>
		(await adb.ScriptAsync($"[ -d {Adb.Q(path)} ] && echo yes || echo no", ct)).Trim() == "yes";

	public static async Task<SyncPlan> PlanAsync(Adb adb, string localRoot, bool includeSave, IProgress<string> status, CancellationToken ct)
	{
		localRoot = Path.GetFullPath(localRoot).TrimEnd('\\');
		string name = Path.GetFileName(localRoot);
		string remoteDir = $"{Device.GamesDir}/{name}";

		status.Report("PC 側のファイルを数えています…");
		var local = await Task.Run(() => LocalIndex(localRoot, ct), ct);

		status.Report("端末の様子を見ています…");
		bool exists = await RemoteDirExistsAsync(adb, remoteDir, ct);
		var plan = new SyncPlan
		{
			Name = name, LocalRoot = localRoot, ExistsOnDevice = exists,
			LocalCount = local.Count, LocalBytes = local.Values.Sum(f => f.Size),
		};

		if (!exists)
		{
			// 初回は全部。sav/ もそのまま送る (端末に上書きされるセーブが無いので)
			plan.Send.AddRange(local.Values.OrderBy(f => f.Rel, StringComparer.Ordinal));
			plan.Added = plan.Send.Count;
			return plan;
		}

		status.Report("端末側のファイルを数えています…");
		var remote = await RemoteIndexAsync(adb, remoteDir, ct);

		await Task.Run(() => Diff(plan, local, remote, includeSave), ct);
		plan.BackupSave = includeSave && plan.Send.Any(f => SavePattern.IsMatch(f.Rel)) &&
			remote.Keys.Any(k => SavePattern.IsMatch(k));
		return plan;
	}

	static void Diff(SyncPlan plan, Dictionary<string, LocalFile> local, Dictionary<string, (long Size, long Time)> remote, bool includeSave)
	{
		// 端末の名前を大文字小文字を無視して引けるようにする (ディレクトリも含めて)
		var remoteCi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string p in remote.Keys)
		{
			int i = -1;
			while (true)
			{
				i = p.IndexOf('/', i + 1);
				string prefix = i < 0 ? p : p[..i];
				remoteCi.TryAdd(prefix, prefix);
				if (i < 0) break;
			}
		}

		// PC の相対パス → 端末での今の相対パス。ずれていたら改名を積む
		var renames = new Dictionary<string, string>(StringComparer.Ordinal); // PC の綴り → 端末の今の名前 (末端だけ)
		var targets = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (rel, l) in local)
		{
			string target = rel;
			if (!remote.ContainsKey(rel))
			{
				var parts = rel.Split('/');
				var acc = new StringBuilder();
				for (int i = 0; i < parts.Length; i++)
				{
					if (i > 0) acc.Append('/');
					acc.Append(parts[i]);
					if (remoteCi.TryGetValue(acc.ToString(), out string? actual))
					{
						string leaf = actual[(actual.LastIndexOf('/') + 1)..];
						if (!string.Equals(leaf, parts[i], StringComparison.Ordinal))
							renames[string.Join('/', parts, 0, i + 1)] = leaf;
						acc.Clear().Append(actual);
					}
					else
					{
						// ここから先は端末に無い。残りは PC の綴りのまま
						for (int j = i + 1; j < parts.Length; j++) acc.Append('/').Append(parts[j]);
						break;
					}
				}
				target = acc.ToString();
			}
			targets.Add(target);

			bool has = remote.TryGetValue(target, out var r);
			// 時刻は「PC のほうが新しい」ときだけ差とみなす。以前 cp (-p なし) で入れたものは
			// 端末側の時刻が入れた日時になっているので、一致を求めると全部送り直しになる
			bool differs = !has || r.Size != l.Size || l.Time > r.Time;
			if (!differs) continue;
			if (!includeSave && SavePattern.IsMatch(rel)) { plan.SkippedSave++; continue; }
			plan.Send.Add(l);
			if (has) plan.Changed++; else plan.Added++;
		}
		plan.Send.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
		foreach (string k in remote.Keys.Where(k => !targets.Contains(k)).Order(StringComparer.Ordinal))
		{
			if (MirrorPattern.IsMatch(k)) plan.Delete.Add(k);
			else if (!SavePattern.IsMatch(k)) plan.OnlyRemote++;
		}

		// 浅い順に並べる。改名前のパスは「PC の綴りの親 / 端末の今の名前」(親は先に直っている)
		foreach (var (pcPath, leaf) in renames.OrderBy(x => x.Key.Count(c => c == '/')).ThenBy(x => x.Key, StringComparer.Ordinal))
		{
			int slash = pcPath.LastIndexOf('/');
			string old = slash < 0 ? leaf : pcPath[..(slash + 1)] + leaf;
			plan.Renames.Add((old, pcPath));
		}
	}

	/// <summary>計画どおりに送る。</summary>
	public static async Task ExecuteAsync(Adb adb, SyncPlan plan, IProgress<SyncProgress> progress, Action<string> log, CancellationToken ct)
	{
		string remoteDir = $"{Device.GamesDir}/{plan.Name}";
		long total = plan.SendBytes;
		int totalFiles = plan.Send.Count;

		await adb.ScriptAsync($"mkdir -p {Adb.Q(Device.GamesDir)}\n{GrantAppAccess(Device.FilesDir)}\n{GrantAppAccess(Device.GamesDir)}", ct);

		if (plan.Delete.Count > 0)
		{
			// 改名より先に消す (消す一覧は端末の今の綴りで持っている)
			progress.Report(new(0, total, 0, totalFiles, "端末にだけある ERB / CSV を消しています…"));
			var sb = new StringBuilder();
			foreach (string rel in plan.Delete) sb.Append($"rm -f {Adb.Q($"{remoteDir}/{rel}")}\n");
			// 空になったフォルダも消す。ERB/ CSV/ そのものは残す (中身が空でもアプリはフォルダの有無で判定する)
			foreach (string top in plan.Delete.Select(d => d[..d.IndexOf('/')]).Distinct(StringComparer.Ordinal))
				sb.Append($"find {Adb.Q($"{remoteDir}/{top}")} -mindepth 1 -depth -type d -empty -delete >/dev/null 2>&1\n");
			sb.Append("true\n");
			await adb.ScriptAsync(sb.ToString(), ct);
			log($"端末にだけある ERB / CSV を {plan.Delete.Count} 個消しました。");
			if (plan.Send.Count == 0 && plan.Renames.Count == 0) await adb.ScriptAsync(GrantAppAccess(remoteDir, recurse: true), ct);
		}

		if (plan.Renames.Count > 0)
		{
			progress.Report(new(0, total, 0, totalFiles, "端末側の名前を合わせています…"));
			// 端末のストレージは大文字小文字を区別しないことがあり、mv csv CSV は「同じもの」で失敗しうる。いったん別名を挟む
			var sb = new StringBuilder();
			foreach (var (old, @new) in plan.Renames)
			{
				string o = Adb.Q($"{remoteDir}/{old}"), t = Adb.Q($"{remoteDir}/{old}.zz_recase"), n = Adb.Q($"{remoteDir}/{@new}");
				sb.Append($"mv {o} {t} && mv {t} {n} || exit 1\n");
				log($"名前を合わせる: {old} → {@new}");
			}
			await adb.ScriptAsync(sb.ToString(), ct);
			if (plan.Send.Count == 0) await adb.ScriptAsync(GrantAppAccess(remoteDir, recurse: true), ct);
		}

		if (plan.BackupSave)
		{
			progress.Report(new(0, total, 0, totalFiles, "端末のセーブを PC に控えています…"));
			string saved = await BackupSaveAsync(adb, plan.Name, ct);
			log($"端末のセーブを控えました: {saved}");
		}

		if (totalFiles == 0) return;

		// 送るものを束に分ける。束ごとに push → 合流させるので、途中で止めても端末側は束の単位で整っている
		var batches = new List<List<LocalFile>>();
		var cur = new List<LocalFile>();
		long curBytes = 0;
		foreach (var f in plan.Send)
		{
			if (cur.Count > 0 && (curBytes + f.Size > BatchBytes || cur.Count >= BatchFiles))
			{
				batches.Add(cur);
				cur = [];
				curBytes = 0;
			}
			cur.Add(f);
			curBytes += f.Size;
		}
		if (cur.Count > 0) batches.Add(cur);

		string stage = CreateLocalStage(plan.LocalRoot);
		long doneBytes = 0;
		int doneFiles = 0;
		try
		{
			for (int b = 0; b < batches.Count; b++)
			{
				ct.ThrowIfCancellationRequested();
				var batch = batches[b];
				progress.Report(new(doneBytes, total, doneFiles, totalFiles, $"送っています… ({b + 1}/{batches.Count})"));

				await Task.Run(() => FillStage(stage, batch, ct), ct);
				await adb.ScriptAsync($"rm -rf {Adb.Q(StageRemote)}", ct);
				await PushCheckedAsync(adb, stage, StageRemote, batch.Count, ct);
				await adb.ScriptAsync(MergeScript(StageRemote, remoteDir), ct);
				ClearDirectory(stage);

				doneBytes += batch.Sum(f => f.Size);
				doneFiles += batch.Count;
				progress.Report(new(doneBytes, total, doneFiles, totalFiles, $"送っています… ({b + 1}/{batches.Count})"));
			}
		}
		finally
		{
			try { Directory.Delete(stage, true); } catch { }
			try { await adb.ScriptAsync($"rm -rf {Adb.Q(StageRemote)}", CancellationToken.None); } catch { }
			// 途中で止めても、合流させたぶんはアプリが読めるようにしておく
			try { await adb.ScriptAsync(GrantAppAccess(remoteDir, recurse: true), CancellationToken.None); } catch { }
		}
	}

	/// <summary>
	/// 端末のゲームのフォルダにアプリが入れるようにする。送るものが無いときに呼ぶ
	/// (以前の版で送ったフォルダは権限が付いておらず、アプリが起動時に落ちることがある)。
	/// </summary>
	public static Task<string> RepairAccessAsync(Adb adb, string gameName, CancellationToken ct) =>
		adb.ScriptAsync(
			$"{GrantAppAccess(Device.FilesDir)}\n{GrantAppAccess(Device.GamesDir)}\n" +
			GrantAppAccess($"{Device.GamesDir}/{gameName}", recurse: true), ct);

	/// <summary>
	/// adb (shell) が作ったフォルダにアプリが入れるようにするコマンド。
	/// shell が作るフォルダは 2770 (持ち主 shell・グループ ext_data_rw) になり、アプリのプロセスは
	/// ext_data_rw に入っていないので中を読めない。アプリを入れてから一度も起動しないうちに
	/// games/ を作ってしまうと、起動するたびに UnauthorizedAccessException で落ちる (実機で起きた)。
	/// 親の Android/data/&lt;pkg&gt; はアプリ専用 (2770) なので、中を o+rwx にしても他のアプリからは見えない。
	/// アプリが作ったものは shell では chmod できないので、失敗は無視する。
	/// </summary>
	static string GrantAppAccess(string path, bool recurse = false) =>
		$"chmod {(recurse ? "-R " : "")}o+rwX {Adb.Q(path)} 2>/dev/null; true";

	/// <summary>
	/// ディレクトリを push し、全部届いたか確かめる。adb はパスが長すぎて読めないファイル
	/// (Windows の 260 文字制限) を「cannot lstat」と言って飛ばし、それでも終了コード 0 を返すので、
	/// 「N files pushed」の数と出力中の adb: error を見る。
	/// </summary>
	static async Task PushCheckedAsync(Adb adb, string from, string to, int expected, CancellationToken ct)
	{
		string output = await adb.CheckedAsync(ct, "push", from, to);
		var m = PushedRegex().Match(output);
		int pushed = m.Success ? int.Parse(m.Groups[1].Value) : -1;
		if (pushed == expected && !output.Contains("adb: error")) return;

		var errors = output.Split('\n').Where(l => l.Contains("adb: error")).Take(5).ToList();
		bool longPath = errors.Any(l => l.Contains("cannot lstat"));
		throw new AdbException(
			$"一部のファイルを送れませんでした ({expected} 個のうち {Math.Max(pushed, 0)} 個)。\n" +
			(longPath ? "ファイルの場所 (パス) が長すぎる可能性があります。ゲームのフォルダを浅い場所 (例: D:\\era\\) に移してから送り直してください。\n" : "") +
			string.Join('\n', errors));
	}

	/// <summary>
	/// 端末上で、送ってきた一時フォルダの中身をゲームフォルダへ合流させる。
	/// cp で写すと容量を 2 倍使い時間もかかるので、mv で付け替える (同じストレージ内なので一瞬)。
	/// mv は更新時刻を保つので、次回の突き合わせでも一致する。
	/// </summary>
	static string MergeScript(string from, string to) => $$"""
		merge() {
		  local e n
		  for e in "$1"/* "$1"/.[!.]*; do
		    [ -e "$e" ] || continue
		    n=${e##*/}
		    if [ -d "$e" ] && [ -d "$2/$n" ]; then
		      merge "$e" "$2/$n" || return 1
		    else
		      rm -rf "$2/$n" && mv "$e" "$2/$n" || return 1
		    fi
		  done
		}
		if [ -d {{Adb.Q(to)}} ]; then
		  merge {{Adb.Q(from)}} {{Adb.Q(to)}} || exit 1
		  rm -rf {{Adb.Q(from)}}
		else
		  mv {{Adb.Q(from)}} {{Adb.Q(to)}} || exit 1
		fi
		""";

	/// <summary>
	/// ローカルの一時フォルダ。ゲームと同じドライブに作れればハードリンクで並べられる (コピーが要らない)。
	/// 作れなければ %TEMP% に作り、そのときはコピーになる。
	/// </summary>
	static string CreateLocalStage(string localRoot)
	{
		string name = $".aes{Environment.ProcessId}";
		string? parent = Path.GetDirectoryName(localRoot);
		string? driveRoot = Path.GetPathRoot(localRoot);
		// パスはなるべく短くする。adb は 260 文字を超えるパスを読めない (cannot lstat で飛ばす)。
		// ドライブ直下 (C:\ は普通は書けない) → ゲームの隣 → %TEMP% の順
		foreach (string? dir in new[]
		{
			string.IsNullOrEmpty(driveRoot) ? null : Path.Combine(driveRoot, name),
			parent != null ? Path.Combine(parent, name) : null,
			Path.Combine(Path.GetTempPath(), name),
		})
		{
			if (dir == null) continue;
			try
			{
				if (Directory.Exists(dir)) Directory.Delete(dir, true);
				var info = Directory.CreateDirectory(dir);
				info.Attributes |= FileAttributes.Hidden;
				return dir;
			}
			catch (Exception) when (dir != Path.Combine(Path.GetTempPath(), name)) { }
		}
		throw new InvalidOperationException("一時フォルダを作れませんでした。");
	}

	static void FillStage(string stage, List<LocalFile> batch, CancellationToken ct)
	{
		foreach (var f in batch)
		{
			ct.ThrowIfCancellationRequested();
			string dst = Path.Combine(stage, f.Rel.Replace('/', '\\'));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			// 読み取り専用のファイルはハードリンクにしない (消すときに属性を外すと元のファイルまで変わる)
			bool readOnly = (File.GetAttributes(f.FullPath) & FileAttributes.ReadOnly) != 0;
			if (readOnly || !CreateHardLink(dst, f.FullPath, IntPtr.Zero))
				File.Copy(f.FullPath, dst, true); // File.Copy は更新時刻を保つ
		}
	}

	static void ClearDirectory(string dir)
	{
		foreach (string sub in Directory.GetDirectories(dir)) Directory.Delete(sub, true);
		foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
	}

	/// <summary>セーブの控え場所 (ドキュメント\andEmuera\セーブの控え)。</summary>
	public static string BackupRoot => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "andEmuera", "セーブの控え");

	/// <summary>
	/// 端末の sav/ を PC へ控える。控えた場所を返す。
	/// pull も remote 側が日本語のディレクトリだと怪しいので、端末上で ASCII 名へ複製してから引く。
	/// </summary>
	public static async Task<string> BackupSaveAsync(Adb adb, string gameName, CancellationToken ct)
	{
		string remoteSav = $"{Device.GamesDir}/{gameName}/sav";
		string tmpRemote = $"{Device.GamesDir}/{Device.StagePrefix}sav";
		string exists = await adb.ScriptAsync($"[ -d {Adb.Q(remoteSav)} ] && echo yes || echo no", ct);
		if (exists.Trim() != "yes") throw new AdbException($"端末の {gameName} にはセーブ (sav フォルダ) がありません。");

		string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
		string dest = Path.Combine(BackupRoot, gameName, stamp);
		string tmpLocal = Path.Combine(Path.GetTempPath(), $"andemuera-sav-{stamp}-{Environment.ProcessId}");
		try
		{
			await adb.ScriptAsync($"rm -rf {Adb.Q(tmpRemote)} && cp -rp {Adb.Q(remoteSav)} {Adb.Q(tmpRemote)}", ct);
			await adb.CheckedAsync(ct, "pull", "-a", tmpRemote, tmpLocal);
			CopyDirectory(tmpLocal, dest);
		}
		finally
		{
			try { await adb.ScriptAsync($"rm -rf {Adb.Q(tmpRemote)}", CancellationToken.None); } catch { }
			try { if (Directory.Exists(tmpLocal)) Directory.Delete(tmpLocal, true); } catch { }
		}
		return dest;
	}

	static void CopyDirectory(string from, string to)
	{
		Directory.CreateDirectory(to);
		foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
		foreach (string dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

	[GeneratedRegex(@"^(?i)sav/")] private static partial Regex SaveRegex();
	[GeneratedRegex(@"^(?i)(erb|csv)/")] private static partial Regex MirrorRegex();
	[GeneratedRegex(@"^(\d+) (\d+)(?:\.\d*)? (.+)$")] private static partial Regex RemoteLineRegex();
	[GeneratedRegex(@"(\d+) files? pushed")] private static partial Regex PushedRegex();
}
