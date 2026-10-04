using System.Diagnostics;

namespace AndEmuera.Installer;

/// <summary>
/// 1 画面で「端末を選ぶ → andEmuera を入れる → ゲームを送る」を順にやる。
/// 端末の抜き差しは数秒おきに見ているので、つなげば勝手に出てくる。
/// </summary>
sealed class MainForm : Form
{
	readonly Adb? adb;
	readonly string? bundledApk;
	readonly ApkInfo? bundledApkInfo;

	// 1. 端末
	readonly ComboBox deviceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
	readonly Label deviceInfo = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
	readonly LinkLabel helpLink = new() { AutoSize = true, Text = "端末が出てこないときは", Padding = new Padding(0, 4, 0, 0) };

	// 2. アプリ
	readonly Label appInfo = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
	readonly Button installButton = new() { AutoSize = true, Text = "andEmuera を入れる" };
	readonly Button chooseApkButton = new() { AutoSize = true, Text = "APK を選んで入れる…" };
	readonly Button launchButton = new() { AutoSize = true, Text = "andEmuera を起動" };

	// 3. ゲーム
	readonly ListView gameList = new()
	{
		View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
		Dock = DockStyle.Fill, HideSelection = false,
	};
	readonly Button sendButton = new() { AutoSize = true, Text = "フォルダを選んで送る…" };
	readonly CheckBox sendSaveBox = new() { AutoSize = true, Text = "セーブ (sav) も送る" };
	readonly Button backupButton = new() { AutoSize = true, Text = "選んだゲームのセーブを PC に保存" };

	// 下段
	readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Height = 22 };
	readonly Label statusLabel = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
	readonly Button cancelButton = new() { AutoSize = true, Text = "中止", Enabled = false };
	readonly TextBox logBox = new()
	{
		Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true,
	};

	readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 2500 };
	CancellationTokenSource? busyCts;
	bool polling;
	List<AdbDevice> devices = [];
	DeviceInfo? info;
	string? lastFolder;
	/// <summary>読み直しの世代。抜き差しで読み直しが重なったとき、古いほうの結果を捨てる。</summary>
	int refreshGen;
	bool closeAfterBusy;

	public MainForm()
	{
		Text = "andEmuera かんたん転送";
		Font = new Font("Yu Gothic UI", 10f);
		ClientSize = new Size(760, 720);
		MinimumSize = new Size(640, 600);
		StartPosition = FormStartPosition.CenterScreen;

		adb = Adb.Find() is { } path ? new Adb(path) : null;
		bundledApk = FindBundledApk();
		try { bundledApkInfo = bundledApk != null ? ApkInfo.Read(bundledApk) : null; } catch { bundledApkInfo = null; }

		BuildLayout();
		WireEvents();
		UpdateControls();

		if (adb == null)
		{
			Log("adb.exe が見つかりません。このツールに同梱の adb フォルダごと置いてください。");
			statusLabel.Text = "adb.exe が見つかりません。";
		}
		else
		{
			Log($"adb: {adb.ExePath}");
			pollTimer.Start();
			_ = PollDevicesAsync();
		}
		Log(bundledApkInfo != null ? $"同梱の andEmuera: {bundledApkInfo.VersionName} ({Path.GetFileName(bundledApk)})"
			: bundledApk != null ? $"同梱の APK を読めませんでした: {Path.GetFileName(bundledApk)}"
			: "同梱の APK が見つかりません (「APK を選んで入れる」で選べます)。");
	}

	// ------------------------------------------------------------ 画面

	void BuildLayout()
	{
		var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
		root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
		root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

		// 1. 端末
		var g1 = Group("1. 端末");
		var t1 = Table(2);
		t1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		t1.Controls.Add(deviceBox, 0, 0);
		t1.Controls.Add(helpLink, 1, 0);
		t1.Controls.Add(deviceInfo, 0, 1);
		t1.SetColumnSpan(deviceInfo, 2);
		g1.Controls.Add(t1);

		// 2. アプリ
		var g2 = Group("2. andEmuera");
		var t2 = Table(1);
		t2.Controls.Add(appInfo, 0, 0);
		t2.Controls.Add(Flow(installButton, chooseApkButton, launchButton), 0, 1);
		g2.Controls.Add(t2);

		// 3. ゲーム
		var g3 = Group("3. ゲーム (era バリアント)");
		g3.Dock = DockStyle.Fill;
		var t3 = Table(1);
		t3.Dock = DockStyle.Fill;
		t3.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		t3.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		t3.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		t3.Controls.Add(Flow(sendButton, sendSaveBox), 0, 0);
		gameList.Columns.Add("端末に入っているゲーム", 420);
		gameList.Columns.Add("状態", 220);
		t3.Controls.Add(gameList, 0, 1);
		t3.Controls.Add(Flow(backupButton), 0, 2);
		g3.Controls.Add(t3);

		// 進捗
		var t4 = Table(2);
		t4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		t4.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		t4.Controls.Add(progressBar, 0, 0);
		t4.Controls.Add(cancelButton, 1, 0);
		t4.Controls.Add(statusLabel, 0, 1);
		t4.SetColumnSpan(statusLabel, 2);

		root.Controls.Add(g1, 0, 0);
		root.Controls.Add(g2, 0, 1);
		root.Controls.Add(g3, 0, 2);
		root.Controls.Add(t4, 0, 3);
		root.Controls.Add(logBox, 0, 4);
		Controls.Add(root);
	}

	static GroupBox Group(string text) => new()
	{
		Text = text, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
		Padding = new Padding(8, 4, 8, 8),
	};

	static TableLayoutPanel Table(int columns) => new()
	{
		ColumnCount = columns, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
	};

	static FlowLayoutPanel Flow(params Control[] controls)
	{
		var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
		foreach (var c in controls)
		{
			c.Margin = new Padding(0, 4, 8, 4);
			f.Controls.Add(c);
		}
		return f;
	}

	void WireEvents()
	{
		pollTimer.Tick += async (_, _) => await PollDevicesAsync();
		deviceBox.SelectedIndexChanged += async (_, _) => await RefreshDeviceAsync();
		helpLink.LinkClicked += (_, _) => ShowHelp();
		installButton.Click += async (_, _) => { if (bundledApk != null) await InstallAsync(bundledApk); };
		chooseApkButton.Click += async (_, _) => await ChooseApkAsync();
		launchButton.Click += async (_, _) => await RunBusyAsync("起動しています…", async ct =>
		{
			await Device.LaunchAsync(adb!, ct);
			Log("andEmuera を起動しました。");
		});
		sendButton.Click += async (_, _) => await SendGameAsync();
		backupButton.Click += async (_, _) => await BackupAsync();
		gameList.SelectedIndexChanged += (_, _) => UpdateControls();
		cancelButton.Click += (_, _) =>
		{
			busyCts?.Cancel();
			cancelButton.Enabled = false;
			statusLabel.Text = "中止しています…";
		};
		FormClosing += (_, e) =>
		{
			if (busyCts == null) return;
			// 途中で閉じると端末側に一時フォルダが残るので、中止の後始末が終わってから閉じる
			e.Cancel = true;
			if (closeAfterBusy) return;
			var r = MessageBox.Show(this, "作業中です。中止して閉じますか?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
			if (r != DialogResult.Yes) return;
			closeAfterBusy = true;
			busyCts.Cancel();
			statusLabel.Text = "中止しています…";
		};
	}

	bool Busy => busyCts != null;
	AdbDevice? SelectedDevice => deviceBox.SelectedItem as AdbDevice;
	bool DeviceReady => adb != null && SelectedDevice is { Ready: true } && info != null;

	void UpdateControls()
	{
		bool idle = !Busy;
		deviceBox.Enabled = idle;
		installButton.Enabled = idle && DeviceReady && bundledApk != null;
		chooseApkButton.Enabled = idle && DeviceReady;
		launchButton.Enabled = idle && DeviceReady && info!.AppInstalled;
		sendButton.Enabled = idle && DeviceReady;
		sendSaveBox.Enabled = idle;
		backupButton.Enabled = idle && DeviceReady && gameList.SelectedItems.Count == 1;
		cancelButton.Enabled = Busy && busyCts is { IsCancellationRequested: false };

		installButton.Text = info is { AppInstalled: true } ? "andEmuera を更新する" : "andEmuera を入れる";
		if (bundledApkInfo != null) installButton.Text += $" ({bundledApkInfo.VersionName})";
	}

	void Log(string message)
	{
		logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message.Replace("\n", Environment.NewLine)}{Environment.NewLine}");
	}

	// ------------------------------------------------------------ 端末

	/// <summary>つながっている端末を見直す。変わっていなければ何もしない。</summary>
	async Task PollDevicesAsync()
	{
		if (adb == null || polling || Busy) return;
		polling = true;
		try
		{
			var now = await adb.DevicesAsync();
			if (now.SequenceEqual(devices)) return;
			devices = now;

			string? keep = SelectedDevice?.Serial;
			deviceBox.BeginUpdate();
			deviceBox.Items.Clear();
			foreach (var d in now) deviceBox.Items.Add(d);
			deviceBox.EndUpdate();

			if (now.Count == 0)
			{
				info = null;
				deviceInfo.Text = "端末が見つかりません。USB デバッグを有効にした端末を USB でつないでください。";
				gameList.Items.Clear();
				appInfo.Text = "";
				UpdateControls();
				return;
			}
			// 前に選んでいたもの → 使える最初の 1 台 → 先頭
			int idx = now.FindIndex(d => d.Serial == keep);
			if (idx < 0) idx = now.FindIndex(d => d.Ready);
			if (idx < 0) idx = 0;
			deviceBox.SelectedIndex = idx; // SelectedIndexChanged で RefreshDeviceAsync が走る
		}
		catch (Exception ex)
		{
			statusLabel.Text = "端末の一覧を取れませんでした: " + ex.Message;
		}
		finally
		{
			polling = false;
		}
	}

	/// <summary>選んでいる端末の様子を読み直す。</summary>
	async Task RefreshDeviceAsync()
	{
		if (adb == null) return;
		var dev = SelectedDevice;
		int gen = ++refreshGen;
		info = null;
		gameList.Items.Clear();
		appInfo.Text = "";
		if (dev == null) { UpdateControls(); return; }
		adb.Serial = dev.Serial;

		if (!dev.Ready)
		{
			deviceInfo.Text = dev.State == "unauthorized"
				? "端末の画面に「USB デバッグを許可しますか?」が出ているので、「許可」を押してください。"
				: $"この端末は今使えません ({dev.State})。USB をつなぎ直してください。";
			UpdateControls();
			return;
		}

		deviceInfo.Text = "端末の様子を見ています…";
		try
		{
			var got = await Device.QueryAsync(adb);
			if (gen != refreshGen) return;
			info = got;
		}
		catch (Exception ex)
		{
			if (gen != refreshGen) return;
			deviceInfo.Text = "端末の様子を読めませんでした: " + ex.Message;
			UpdateControls();
			return;
		}
		ShowInfo();
	}

	void ShowInfo()
	{
		if (info == null) return;
		string free = info.FreeBytes >= 0 ? $"空き {FormatBytes(info.FreeBytes)}" : "空き容量不明";
		deviceInfo.Text = $"{info.Model}　Android {info.AndroidVersion}　{free}";

		if (info.AppInstalled)
		{
			string kind = info.AppDebuggable ? " (開発版)" : "";
			appInfo.Text = $"入っている版: {info.AppVersionName} (versionCode {info.AppVersionCode}){kind}";
		}
		else
		{
			appInfo.Text = "andEmuera はまだ入っていません。";
		}

		gameList.BeginUpdate();
		gameList.Items.Clear();
		foreach (var g in info.Games)
		{
			var item = new ListViewItem(g.Name) { Tag = g };
			item.SubItems.Add(g.Playable ? "遊べます" : "csv / erb が見つかりません");
			gameList.Items.Add(item);
		}
		gameList.EndUpdate();
		if (info.Games.Count == 0)
		{
			var item = new ListViewItem("(まだ何も入っていません)") { ForeColor = SystemColors.GrayText };
			gameList.Items.Add(item);
		}
		UpdateControls();
	}

	void ShowHelp()
	{
		MessageBox.Show(this, """
			端末を PC から操作できるようにする手順です (最初の 1 回だけ)。

			1. 端末の「設定」→「デバイス情報」(または「端末情報」) を開き、
			   「ビルド番号」を 7 回タップする (開発者向けオプションが出ます)
			2. 「設定」→「開発者向けオプション」で「USB デバッグ」を オン にする
			3. USB ケーブルで PC とつなぐ
			4. 端末の画面に「USB デバッグを許可しますか?」が出たら「許可」を押す
			   (「このパソコンからの接続を常に許可」にチェックしておくと次回から聞かれません)

			それでも出てこないときは:
			・ケーブルが充電専用でないか確かめる (データ通信できるものが必要です)
			・端末の通知から USB の用途を「ファイル転送」にしてみる
			・機種によっては、メーカーの USB ドライバを PC に入れる必要があります
			""", "端末をつなぐには", MessageBoxButtons.OK, MessageBoxIcon.Information);
	}

	// ------------------------------------------------------------ 共通

	/// <summary>時間のかかる作業を、ほかのボタンを止めて走らせる。</summary>
	async Task RunBusyAsync(string status, Func<CancellationToken, Task> work, bool refreshAfter = true)
	{
		if (Busy) return;
		using var cts = new CancellationTokenSource();
		busyCts = cts;
		pollTimer.Stop();
		statusLabel.Text = status;
		progressBar.Style = ProgressBarStyle.Marquee;
		UpdateControls();
		try
		{
			await work(cts.Token);
		}
		catch (OperationCanceledException)
		{
			Log("中止しました。");
			statusLabel.Text = "中止しました。";
		}
		catch (Exception ex)
		{
			Log("失敗: " + ex.Message);
			statusLabel.Text = "失敗しました。";
			MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			busyCts = null;
			progressBar.Style = ProgressBarStyle.Blocks;
			UpdateControls();
			pollTimer.Start();
		}
		if (closeAfterBusy) { BeginInvoke(Close); return; }
		if (refreshAfter) await RefreshDeviceAsync();
	}

	static string FormatBytes(long b) => b switch
	{
		>= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
		>= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
		>= 1L << 10 => $"{b / 1024.0:0} KB",
		_ => $"{b} B",
	};

	// ------------------------------------------------------------ 2. アプリ

	/// <summary>exe の隣にある APK (配布 zip に同梱しているもの)。複数あれば新しいもの。</summary>
	static string? FindBundledApk() =>
		Directory.GetFiles(AppContext.BaseDirectory, "*.apk")
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();

	async Task ChooseApkAsync()
	{
		using var dlg = new OpenFileDialog { Filter = "Android アプリ (*.apk)|*.apk", Title = "入れる APK を選んでください" };
		if (dlg.ShowDialog(this) != DialogResult.OK) return;
		await InstallAsync(dlg.FileName);
	}

	async Task InstallAsync(string apk)
	{
		ApkInfo apkInfo;
		try
		{
			apkInfo = ApkInfo.Read(apk);
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, $"APK を読めませんでした。\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
			return;
		}
		Log($"APK: {Path.GetFileName(apk)} = {apkInfo.VersionName} (versionCode {apkInfo.VersionCode})");

		// 危ないものは adb を呼ぶ前に止める (adb に失敗させると、アンインストールして入れ直すことがある)
		if (Device.CheckInstallable(apkInfo, info) is { } problem)
		{
			Log("入れませんでした: " + problem);
			MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		string what = info is { AppInstalled: true }
			? $"andEmuera を {info.AppVersionName} から {apkInfo.VersionName} に更新します。"
			: $"andEmuera {apkInfo.VersionName} を入れます。";
		bool hasGames = info is { Games.Count: > 0 };
		DialogResult answer;
		if (hasGames)
		{
			// 上書きでは端末のゲームとセーブは消えない。それでも万一に備えて控えを勧める
			answer = MessageBox.Show(this,
				$"{what}\n\n念のため、端末に入っているゲームのセーブを先に PC に控えますか?\n" +
				$"(控え先: {GameSync.BackupRoot})\n\n" +
				"「はい」… 控えてから入れる\n「いいえ」… 控えずに入れる",
				Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
			if (answer == DialogResult.Cancel) return;
		}
		else
		{
			if (MessageBox.Show(this, what, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
			answer = DialogResult.No;
		}

		await RunBusyAsync($"{Path.GetFileName(apk)} を入れています… (1 分ほどかかります)", async ct =>
		{
			if (answer == DialogResult.Yes)
			{
				statusLabel.Text = "セーブを PC に控えています…";
				await BackupAllSavesAsync(ct);
				statusLabel.Text = $"{Path.GetFileName(apk)} を入れています… (1 分ほどかかります)";
			}
			Log($"インストール: {apk}");
			await Device.InstallAsync(adb!, apk, apkInfo, info, ct);
			Log("andEmuera を入れました。");
			statusLabel.Text = "andEmuera を入れました。";
		});
	}

	/// <summary>端末の全ゲームのセーブを PC に控える。セーブの無いゲームは飛ばす。</summary>
	async Task BackupAllSavesAsync(CancellationToken ct)
	{
		foreach (var g in info?.Games ?? [])
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				string saved = await GameSync.BackupSaveAsync(adb!, g.Name, ct);
				Log($"セーブを控えました: {saved}");
			}
			catch (AdbException ex) when (ex.Message.Contains("ありません"))
			{
				// sav フォルダが無い (まだ遊んでいない) ゲーム
			}
		}
	}

	// ------------------------------------------------------------ 3. ゲーム

	async Task SendGameAsync()
	{
		using var dlg = new FolderBrowserDialog
		{
			Description = "送るゲームのフォルダを選んでください (csv と erb が入っているフォルダ)",
			UseDescriptionForTitle = true,
			InitialDirectory = lastFolder ?? "",
		};
		if (dlg.ShowDialog(this) != DialogResult.OK) return;
		string folder = dlg.SelectedPath;
		lastFolder = Path.GetDirectoryName(folder);

		if (!GameSync.IsGameFolder(folder))
		{
			MessageBox.Show(this,
				$"このフォルダには csv と erb がありません。\n{folder}\n\nEmuera の exe と同じ場所にある、csv と erb を含むフォルダを選んでください。",
				Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		string name = Path.GetFileName(folder.TrimEnd('\\'));
		if (GameSync.ValidateName(name) is { } problem)
		{
			MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		bool includeSave = sendSaveBox.Checked;
		SyncPlan? plan = null;
		await RunBusyAsync("送るものを調べています…", async ct =>
		{
			var status = new Progress<string>(s => statusLabel.Text = s);
			plan = await GameSync.PlanAsync(adb!, folder, includeSave, status, ct);
		}, refreshAfter: false);
		if (plan == null) return;

		Log($"{plan.Name}: PC {plan.LocalCount:N0} ファイル / {FormatBytes(plan.LocalBytes)}");
		if (plan.UpToDate)
		{
			// 送るものが無くても権限は直す。以前の版で送ったフォルダはアプリが読めず、起動時に落ちることがある
			await RunBusyAsync("フォルダの権限を確かめています…", ct => GameSync.RepairAccessAsync(adb!, plan.Name, ct), refreshAfter: false);
			string note = plan.SkippedSave > 0 ? $"\n\n(セーブ {plan.SkippedSave} ファイルは送っていません。送るなら「セーブ (sav) も送る」にチェック)" : "";
			Log($"{plan.Name}: 端末は最新です。");
			statusLabel.Text = $"{plan.Name} は端末と同じです。";
			MessageBox.Show(this, $"{plan.Name} は端末と同じです。送るものはありません。{note}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		// 確認
		var lines = new List<string>();
		if (plan.ExistsOnDevice)
		{
			lines.Add($"「{plan.Name}」を更新します。");
			lines.Add("");
			lines.Add($"新しいファイル: {plan.Added:N0}");
			lines.Add($"変わったファイル: {plan.Changed:N0} (PC 側で上書き)");
		}
		else
		{
			lines.Add($"「{plan.Name}」を端末に入れます。");
			lines.Add("");
			lines.Add($"ファイル: {plan.Added:N0}");
		}
		lines.Add($"送る量: {FormatBytes(plan.SendBytes)}");
		if (plan.Renames.Count > 0)
			lines.Add($"名前の大文字小文字を PC に合わせる: {plan.Renames.Count} 件 ({string.Join(", ", plan.Renames.Take(3).Select(r => $"{r.Old} → {r.New}"))}{(plan.Renames.Count > 3 ? " …" : "")})");
		if (plan.SkippedSave > 0)
			lines.Add($"セーブ {plan.SkippedSave} ファイルは送りません (端末で進めたセーブを守るため)");
		if (plan.BackupSave)
			lines.Add("端末のセーブは、上書きする前に PC に控えます");
		if (plan.OnlyRemote > 0)
			lines.Add($"端末にだけあるファイル {plan.OnlyRemote:N0} 個はそのまま残します");
		if (info is { FreeBytes: >= 0 } && plan.SendBytes > info.FreeBytes)
			lines.Add($"\n⚠ 端末の空き ({FormatBytes(info.FreeBytes)}) が足りないかもしれません。");
		lines.Add("");
		lines.Add("送りますか?");
		if (MessageBox.Show(this, string.Join("\n", lines), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
			return;

		var sw = Stopwatch.StartNew();
		await RunBusyAsync("送っています…", async ct =>
		{
			progressBar.Style = ProgressBarStyle.Blocks;
			progressBar.Maximum = 1000;
			progressBar.Value = 0;
			var progress = new Progress<SyncProgress>(p =>
			{
				progressBar.Value = p.TotalBytes > 0 ? (int)(1000 * p.DoneBytes / p.TotalBytes) : 0;
				statusLabel.Text = $"{p.Message}  {p.DoneFiles:N0} / {p.TotalFiles:N0} ファイル  {FormatBytes(p.DoneBytes)} / {FormatBytes(p.TotalBytes)}";
			});
			await GameSync.ExecuteAsync(adb!, plan, progress, s => BeginInvoke(() => Log(s)), ct);
			progressBar.Value = progressBar.Maximum;
			string msg = $"{plan.Name} を送りました ({FormatBytes(plan.SendBytes)}, {sw.Elapsed.TotalSeconds:0} 秒)。";
			Log(msg);
			statusLabel.Text = msg + (info is { AppInstalled: true } ? " 「andEmuera を起動」で遊べます。" : "");
		});
	}

	async Task BackupAsync()
	{
		if (gameList.SelectedItems.Count != 1 || gameList.SelectedItems[0].Tag is not RemoteGame game) return;
		string? saved = null;
		await RunBusyAsync($"{game.Name} のセーブを PC に保存しています…", async ct =>
		{
			saved = await GameSync.BackupSaveAsync(adb!, game.Name, ct);
			Log($"セーブを保存しました: {saved}");
			statusLabel.Text = "セーブを保存しました。";
		}, refreshAfter: false);
		if (saved != null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{saved}\"") { UseShellExecute = true });
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing) pollTimer.Dispose();
		base.Dispose(disposing);
	}
}
