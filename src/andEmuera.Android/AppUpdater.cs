// andEmuera: アプリ自身の更新確認。
//
// 起動したときに GitHub Releases の最新版を調べ、端末のアプリより新しい APK があれば更新するか尋ねる。
// 「更新する」を押すと APK を落として大きさと SHA-256 を確かめ、Android のインストーラーへ渡す。
// Google Play を通していないアプリは黙って入れ替えられないので、最後に Android の確認画面で
// 「更新」を押してもらう (初回は「この提供元のアプリを許可」もオンにしてもらう)。
//
// 新しいかどうかは、リリースに付けた andEmuera-update.json (tools/pack.ps1 が書く) の
// versionCode で決める。かんたん転送だけ直したリリースは versionCode が変わらないので、ここでは知らせない。
//
// 通信するのは api.github.com と、GitHub のリリースファイルの置き場だけ。
// ゲームの中身やセーブはどこにも送らない。

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AndEmuera;

/// <summary>GitHub Releases の最新版に付いている APK。</summary>
sealed record AppRelease(string VersionName, long VersionCode, string PageUrl, string ApkName, string ApkUrl, long ApkSize, string Sha256);

sealed class AppUpdater
{
	const string LogTag = "andEmuera";
	const string Repo = "BluishCat/andEmuera";

	/// <summary>リリースに付ける版の情報。tools/pack.ps1 と名前を合わせる。</summary>
	public const string ManifestAsset = "andEmuera-update.json";

	const string Prefs = "andemuera";
	/// <summary>「この版は入れない」を選んだ versionCode。</summary>
	const string PrefSkipCode = "updateSkipVersionCode";
	/// <summary>「あとで」を選んだとき、次に尋ねてよい時刻 (UNIX ミリ秒)。</summary>
	const string PrefSnoozeUntil = "updateSnoozeUntil";
	static readonly TimeSpan SnoozeSpan = TimeSpan.FromHours(24);

	static readonly HttpClient Http = CreateClient();

	static HttpClient CreateClient()
	{
		var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		// GitHub API は User-Agent の無い要求を断る
		h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("andEmuera", "app"));
		return h;
	}

	/// <summary>インストール結果を画面に出す先。結果はブロードキャストで届くので、今の画面を覚えておく。</summary>
	static WeakReference<AppUpdater> current;

	readonly Activity activity;

	/// <summary>「この提供元のアプリを許可」をオンにしてもらうのを待っている APK。</summary>
	string pendingApk;

	public AppUpdater(Activity activity)
	{
		this.activity = activity;
		current = new WeakReference<AppUpdater>(this);
	}

	ISharedPreferences Preferences => activity.GetSharedPreferences(Prefs, FileCreationMode.Private);

	string UpdateDir => Path.Combine(activity.CacheDir.AbsolutePath, "update");

	/// <summary>
	/// 起動したときに呼ぶ。裏で最新版を調べ、新しければ尋ねる。
	/// 通信できない・GitHub が答えないなどのときは何も出さない (遊ぶのを邪魔しない)。
	/// </summary>
	public void CheckOnStartup()
	{
		// 前回落とした APK は、入れ終わっていても途中でも要らない
		try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); }
		catch (Exception ex) { Android.Util.Log.Warn(LogTag, $"更新の残りを消せません: {ex.Message}"); }

		// 開発版 (Debug、パッケージ名 .debug) は配布版と別のアプリなので、配布版の APK では更新できない。
		// 版も main から入れ直すものなので、確かめない
		if (IsDebugBuild)
		{
			Android.Util.Log.Info(LogTag, "更新確認: 開発版なので確かめません");
			return;
		}

		long snoozeUntil = Preferences.GetLong(PrefSnoozeUntil, 0);
		if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < snoozeUntil)
			return;

		_ = Task.Run(async () =>
		{
			try
			{
				var rel = await GetLatestAsync(CancellationToken.None);
				long own = OwnVersionCode();
				if (rel == null)
				{
					Android.Util.Log.Info(LogTag, "更新確認: 最新のリリースに更新用の APK が付いていません");
					return;
				}
				Android.Util.Log.Info(LogTag, $"更新確認: 端末 {OwnVersionName()} (versionCode {own}) / 最新 {rel.VersionName} (versionCode {rel.VersionCode})");
				if (rel.VersionCode <= own)
					return;
				if (Preferences.GetLong(PrefSkipCode, 0) == rel.VersionCode)
					return;
				activity.RunOnUiThread(() => Ask(rel));
			}
			catch (Exception ex)
			{
				// 機内モードや GitHub の不調。黙って次の起動に任せる
				Android.Util.Log.Info(LogTag, $"更新確認できませんでした: {ex.GetType().Name}: {ex.Message}");
			}
		});
	}

	/// <summary>Activity.OnResume から呼ぶ。設定画面で提供元を許可して戻ってきたら、続きを入れる。</summary>
	public void OnResume()
	{
		if (pendingApk == null || !CanInstallPackages())
			return;
		string apk = pendingApk;
		pendingApk = null;
		_ = InstallAsync(apk);
	}

	// ------------------------------------------------------------ 最新版を調べる

	static async Task<AppRelease> GetLatestAsync(CancellationToken ct)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(TimeSpan.FromSeconds(15));

		using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
		req.Headers.Accept.ParseAdd("application/vnd.github+json");
		using var res = await Http.SendAsync(req, cts.Token);
		res.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
		var r = doc.RootElement;

		var assets = r.GetProperty("assets").EnumerateArray().ToList();
		var manifest = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == ManifestAsset);
		if (manifest.ValueKind != JsonValueKind.Object)
			return null;

		// andEmuera-update.json: {"versionName":"0.94","versionCode":10,"apk":"andEmuera-0.94.apk"}
		string json = await Http.GetStringAsync(manifest.GetProperty("browser_download_url").GetString(), cts.Token);
		using var m = JsonDocument.Parse(json);
		string versionName = m.RootElement.GetProperty("versionName").GetString();
		long versionCode = m.RootElement.GetProperty("versionCode").GetInt64();
		string apkName = m.RootElement.GetProperty("apk").GetString();

		var apk = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == apkName);
		if (apk.ValueKind != JsonValueKind.Object)
			return null;

		// digest は "sha256:<16 進>"。確かめられない APK は入れない
		string digest = apk.TryGetProperty("digest", out var d) ? d.GetString() : null;
		if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
			return null;

		return new AppRelease(
			versionName,
			versionCode,
			r.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases",
			apkName,
			apk.GetProperty("browser_download_url").GetString(),
			apk.GetProperty("size").GetInt64(),
			digest["sha256:".Length..]);
	}

	long OwnVersionCode()
	{
		var info = activity.PackageManager.GetPackageInfo(activity.PackageName, 0);
		return OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
	}

	string OwnVersionName() => activity.PackageManager.GetPackageInfo(activity.PackageName, 0).VersionName;

	/// <summary>開発用 (Debug) のビルドか。配布版とは署名もパッケージ名も違う別のアプリ。</summary>
	bool IsDebugBuild => (activity.ApplicationInfo.Flags & ApplicationInfoFlags.Debuggable) != 0;

	// ------------------------------------------------------------ 尋ねる

	void Ask(AppRelease rel)
	{
		if (activity.IsFinishing || activity.IsDestroyed)
			return;

		string sizeText = $"{rel.ApkSize / 1024.0 / 1024.0:F1} MB";
		var builder = new AlertDialog.Builder(activity)
			.SetTitle($"andEmuera {rel.VersionName} が出ています");

		builder
			.SetMessage(
				$"いまの版: {OwnVersionName()}\n新しい版: {rel.VersionName} ({sizeText})\n\n" +
				"更新すると、Android の確認画面のあとにアプリがいったん終わります。" +
				"遊んでいる途中なら、セーブしてから更新してください。\n" +
				"端末のゲームとセーブはそのまま残ります。")
			.SetPositiveButton("更新する", (s, e) => _ = DownloadAndInstallAsync(rel))
			.SetNeutralButton("この版は入れない", (s, e) => Skip(rel))
			.SetNegativeButton("あとで", (s, e) => Snooze());
		builder.SetCancelable(true).SetOnCancelListener(new CancelListener(Snooze)).Show();
	}

	void Skip(AppRelease rel) => Preferences.Edit().PutLong(PrefSkipCode, rel.VersionCode).Apply();

	void Snooze() => Preferences.Edit()
		.PutLong(PrefSnoozeUntil, DateTimeOffset.UtcNow.Add(SnoozeSpan).ToUnixTimeMilliseconds()).Apply();

	sealed class CancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
	{
		public void OnCancel(IDialogInterface dialog) => onCancel();
	}

	// ------------------------------------------------------------ 落とす

	async Task DownloadAndInstallAsync(AppRelease rel)
	{
		using var cts = new CancellationTokenSource();
		var dialog = new AlertDialog.Builder(activity)
			.SetTitle($"andEmuera {rel.VersionName} を落としています")
			.SetMessage("0%")
			.SetCancelable(false)
			.SetNegativeButton("やめる", (s, e) => cts.Cancel())
			.Show();

		string apk;
		try
		{
			var progress = new Progress<int>(p => dialog.SetMessage($"{p}%"));
			apk = await Task.Run(() => DownloadAsync(rel, progress, cts.Token));
		}
		catch (System.OperationCanceledException)
		{
			dialog.Dismiss();
			return;
		}
		catch (Exception ex)
		{
			dialog.Dismiss();
			Android.Util.Log.Error(LogTag, $"更新をダウンロードできません: {ex}");
			ShowMessage("ダウンロードできませんでした", ex.Message);
			return;
		}
		dialog.Dismiss();

		if (!SameSigner(apk))
		{
			ShowMessage("この端末では上書き更新できません",
				"落とした APK と、この端末に入っている andEmuera の署名が違います。" +
				"入れ替えるにはアンインストールが必要で、そのとき端末のゲームとセーブが消えます。" +
				"先にセーブを PC へ控えてから入れ替えてください。");
			return;
		}

		await InstallAsync(apk);
	}

	async Task<string> DownloadAsync(AppRelease rel, IProgress<int> progress, CancellationToken ct)
	{
		Directory.CreateDirectory(UpdateDir);
		string part = Path.Combine(UpdateDir, rel.ApkName + ".part");
		string path = Path.Combine(UpdateDir, rel.ApkName);
		try
		{
			using var res = await Http.GetAsync(rel.ApkUrl, HttpCompletionOption.ResponseHeadersRead, ct);
			res.EnsureSuccessStatusCode();
			long total = res.Content.Headers.ContentLength ?? rel.ApkSize;
			long done = 0;
			string hash;
			using (var src = await res.Content.ReadAsStreamAsync(ct))
			using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
			using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
			{
				var buf = new byte[1 << 16];
				int n, last = -1;
				// 通信が止まったまま待ち続けないよう、読み 1 回ごとに時間を区切る
				while (true)
				{
					using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
					stall.CancelAfter(TimeSpan.FromSeconds(60));
					n = await src.ReadAsync(buf, stall.Token);
					if (n <= 0)
						break;
					sha.AppendData(buf, 0, n);
					await dst.WriteAsync(buf.AsMemory(0, n), ct);
					done += n;
					int pct = total > 0 ? (int)(done * 100 / total) : 0;
					if (pct != last) { progress.Report(pct); last = pct; }
				}
				hash = Convert.ToHexString(sha.GetHashAndReset());
			}
			if (done != rel.ApkSize)
				throw new InvalidDataException($"ダウンロードが途中で切れました ({done:N0} / {rel.ApkSize:N0} バイト)。");
			if (!hash.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("ダウンロードしたファイルが壊れています (SHA-256 が一致しません)。");
			File.Move(part, path, true);
			return path;
		}
		catch
		{
			try { File.Delete(part); } catch { }
			throw;
		}
	}

	/// <summary>
	/// 落とした APK の署名が、入っているアプリと同じか。違えば Android は上書きを断るので、
	/// 確認画面まで進ませずにここで理由を出す。調べられないときはインストーラーの判断に任せる。
	/// </summary>
	bool SameSigner(string apk)
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(28))
			return true;
		try
		{
			var pm = activity.PackageManager;
#pragma warning disable CA1422 // 33 からの long 版でなく int 版を使う (28 から動く)
			var mine = pm.GetPackageInfo(activity.PackageName, PackageInfoFlags.SigningCertificates)?.SigningInfo;
			var theirs = pm.GetPackageArchiveInfo(apk, PackageInfoFlags.SigningCertificates);
#pragma warning restore CA1422
			if (theirs != null && theirs.PackageName != activity.PackageName)
				return false;
			var a = mine?.GetApkContentsSigners();
			var b = theirs?.SigningInfo?.GetApkContentsSigners();
			if (a == null || b == null || a.Length == 0 || b.Length == 0)
				return true;
			var sa = a.Select(x => Convert.ToBase64String(x.ToByteArray())).OrderBy(x => x, StringComparer.Ordinal);
			var sb = b.Select(x => Convert.ToBase64String(x.ToByteArray())).OrderBy(x => x, StringComparer.Ordinal);
			return sa.SequenceEqual(sb);
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn(LogTag, $"APK の署名を調べられません: {ex.Message}");
			return true;
		}
	}

	// ------------------------------------------------------------ 入れる

	bool CanInstallPackages() =>
		!OperatingSystem.IsAndroidVersionAtLeast(26) || activity.PackageManager.CanRequestPackageInstalls();

	async Task InstallAsync(string apk)
	{
		if (OperatingSystem.IsAndroidVersionAtLeast(26) && !CanInstallPackages())
		{
			// Android 8 以降は、アプリごとに「この提供元のアプリを許可」が要る
			pendingApk = apk;
			new AlertDialog.Builder(activity)
				.SetTitle("インストールの許可が必要です")
				.SetMessage(
					"andEmuera が自分を更新するには、「この提供元のアプリを許可」をオンにする必要があります (最初の 1 回だけ)。\n\n" +
					"設定画面でオンにしたら、戻るボタンでここへ戻ってください。続きから更新します。")
				.SetPositiveButton("設定を開く", (s, e) =>
				{
					if (!OperatingSystem.IsAndroidVersionAtLeast(26))
						return;
					var intent = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
						Android.Net.Uri.Parse("package:" + activity.PackageName));
					activity.StartActivity(intent);
				})
				.SetNegativeButton("やめる", (s, e) => pendingApk = null)
				.Show();
			return;
		}

		try
		{
			await Task.Run(() => Commit(apk));
		}
		catch (Exception ex)
		{
			Android.Util.Log.Error(LogTag, $"インストーラーに渡せません: {ex}");
			ShowMessage("更新を始められませんでした", ex.Message);
		}
	}

	/// <summary>
	/// PackageInstaller のセッションに APK を書いて確定する。結果 (確認画面を出すかどうか・成否) は
	/// <see cref="InstallResultReceiver"/> に届く。
	/// </summary>
	void Commit(string apk)
	{
		var installer = activity.PackageManager.PackageInstaller;
		var param = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
		param.SetAppPackageName(activity.PackageName);
		param.SetSize(new FileInfo(apk).Length);
		// Android 12 以降は、自分で入れた版なら確認なしで入れ替えられることがある
		// (adb で入れた版からの初回は確認画面が出る)
		if (OperatingSystem.IsAndroidVersionAtLeast(31))
			param.SetRequireUserAction((int)PackageInstallUserAction.NotRequired);

		int id = installer.CreateSession(param);
		try
		{
			using var session = installer.OpenSession(id);
			using (var input = File.OpenRead(apk))
			using (var output = session.OpenWrite("base.apk", 0, input.Length))
			{
				input.CopyTo(output);
				session.Fsync(output);
			}

			var intent = new Intent(activity, typeof(InstallResultReceiver));
			intent.SetPackage(activity.PackageName);
			// 結果の extra はシステムが書き足すので Mutable にする (12 以降は明示が要る)
			var flags = PendingIntentFlags.UpdateCurrent;
			if (OperatingSystem.IsAndroidVersionAtLeast(31))
				flags |= PendingIntentFlags.Mutable;
			var pending = PendingIntent.GetBroadcast(activity, id, intent, flags);
			session.Commit(pending.IntentSender);
		}
		catch
		{
			try { installer.AbandonSession(id); } catch { }
			throw;
		}
	}

	internal static void OnInstallResult(Context context, PackageInstallStatus status, string message)
	{
		Android.Util.Log.Info(LogTag, $"更新の結果: {status} {message}");
		if (status == PackageInstallStatus.Success)
			return; // ふつうはここへ来る前にアプリが終わる

		string text = status switch
		{
			PackageInstallStatus.FailureAborted => null, // 確認画面で「キャンセル」
			PackageInstallStatus.FailureConflict =>
				"入っている andEmuera と署名が違うため、上書きできませんでした。" +
				"入れ替えるにはアンインストールが必要で、そのとき端末のゲームとセーブが消えます。",
			PackageInstallStatus.FailureStorage => "端末の空き容量が足りません。",
			PackageInstallStatus.FailureIncompatible => "この端末には入れられない APK でした。",
			PackageInstallStatus.FailureBlocked => "端末の設定によって、インストールが止められました。",
			_ => $"インストールに失敗しました ({status})。\n{message}",
		};
		if (text == null)
			return;

		if (current != null && current.TryGetTarget(out var updater) && !updater.activity.IsDestroyed)
			updater.activity.RunOnUiThread(() => updater.ShowMessage("更新できませんでした", text));
		else
			Toast.MakeText(context, text, ToastLength.Long)?.Show();
	}

	void ShowMessage(string title, string message)
	{
		if (activity.IsFinishing || activity.IsDestroyed)
			return;
		new AlertDialog.Builder(activity)
			.SetTitle(title)
			.SetMessage(message)
			.SetPositiveButton("OK", (s, e) => { })
			.Show();
	}
}

/// <summary>PackageInstaller からの結果を受ける。確認が要るときは Android の確認画面を開く。</summary>
[BroadcastReceiver(Exported = false)]
public sealed class InstallResultReceiver : BroadcastReceiver
{
	public override void OnReceive(Context context, Intent intent)
	{
		var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
		if (status == PackageInstallStatus.PendingUserAction)
		{
			Intent confirm;
#pragma warning disable CA1422
			if (OperatingSystem.IsAndroidVersionAtLeast(33))
				confirm = (Intent)intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent)));
			else
				confirm = (Intent)intent.GetParcelableExtra(Intent.ExtraIntent);
#pragma warning restore CA1422
			if (confirm == null)
				return;
			confirm.AddFlags(ActivityFlags.NewTask);
			context.StartActivity(confirm);
			return;
		}
		AppUpdater.OnInstallResult(context, status, intent.GetStringExtra(PackageInstaller.ExtraStatusMessage));
	}
}
