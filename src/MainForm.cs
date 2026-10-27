using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TwinEars
{
    internal sealed class MainForm : Form
    {
        static readonly Color OnColor = Color.FromArgb(0x1E, 0x8E, 0x4E);
        static readonly Color OffColor = Color.FromArgb(0x5F, 0x67, 0x73);
        static readonly Color GoodText = Color.FromArgb(0x12, 0x7A, 0x3D);
        static readonly Color WarnText = Color.FromArgb(0x9A, 0x5B, 0x00);
        static readonly Color BadText = Color.FromArgb(0xB3, 0x26, 0x1E);

        readonly AppSettings settings;
        readonly MirrorManager manager = new MirrorManager();
        readonly float scale;
        bool mirroringOn;
        bool startHidden;
        bool exiting;
        bool shutDown;
        bool updating;
        bool settingsDirty;
        bool trayHintShown;
        int ticks;

        List<AudioDevice> devices = new List<AudioDevice>();
        string defaultId;
        string deviceSignature;
        volatile string lockedSourceId;
        volatile int lockedExclusiveAllowed = -1;   // the locked device's exclusive-mode checkbox: 1, 0, or -1 unknown
        volatile bool probeRunning;
        int lastProbeTick = Environment.TickCount - 60000;

        Button toggleButton;
        Label statusLabel;
        Label warningLabel;
        ComboBox sourceCombo;
        ListView targetList;
        GroupBox tuneGroup;
        TrackBar delayBar, volumeBar;
        NumericUpDown delayBox, volumeBox;
        Button testButton;
        CheckBox autoOnCheck, trayCheck;
        NotifyIcon tray;
        ToolStripMenuItem trayToggleItem;
        System.Windows.Forms.Timer timer;
        Icon windowIconOn, windowIconOff, trayIconOn, trayIconOff;
        readonly ToolTip tips = new ToolTip();
        readonly List<Label> notes = new List<Label>();
        SoundPlayer testPlayer;
        int testEndTick;

        public MainForm(AppSettings settings, bool turnOn, bool startHidden)
        {
            this.settings = settings;
            this.startHidden = startHidden;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            BuildUi();
            BuildTray();
            RefreshDevices(true);
            if (turnOn) SetMirroring(true);
            else UpdateStatus();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 500;
            timer.Tick += OnTick;
            timer.Start();
        }

        int S(int pixels)
        {
            return (int)Math.Round(pixels * scale);
        }

        // ---------------------------------------------------------------- layout

        void BuildUi()
        {
            Text = "TwinEars";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(560), S(650));
            MinimumSize = new Size(S(480), S(600));
            windowIconOn = AppIcon.Create(true, 32);
            windowIconOff = AppIcon.Create(false, 32);
            int small = SystemInformation.SmallIconSize.Width;
            trayIconOn = AppIcon.Create(true, small);
            trayIconOff = AppIcon.Create(false, small);
            Icon = windowIconOff;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.Padding = new Padding(S(12), S(12), S(12), S(8));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // On/off switch and a one-line summary.
            TableLayoutPanel header = NewGrid(2);
            header.Margin = new Padding(0, 0, 0, S(10));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toggleButton = new Button();
            toggleButton.Size = new Size(S(170), S(48));
            toggleButton.FlatStyle = FlatStyle.Flat;
            toggleButton.FlatAppearance.BorderSize = 0;
            toggleButton.ForeColor = Color.White;
            toggleButton.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            toggleButton.Cursor = Cursors.Hand;
            toggleButton.Margin = new Padding(0, 0, S(12), 0);
            toggleButton.Click += delegate { SetMirroring(!mirroringOn); };
            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.AutoEllipsis = true;
            header.Controls.Add(toggleButton, 0, 0);
            header.Controls.Add(statusLabel, 1, 0);
            root.Controls.Add(header, 0, 0);

            // Only shown when something outside TwinEars needs the user's attention.
            warningLabel = new Label();
            warningLabel.AutoSize = true;
            warningLabel.Dock = DockStyle.Fill;
            warningLabel.BackColor = Color.FromArgb(0xFF, 0xF4, 0xCE);
            warningLabel.ForeColor = Color.FromArgb(0x5C, 0x3B, 0x00);
            warningLabel.Padding = new Padding(S(8), S(6), S(8), S(6));
            warningLabel.Margin = new Padding(0, 0, 0, S(10));
            warningLabel.Visible = false;
            notes.Add(warningLabel);
            root.Controls.Add(warningLabel, 0, 1);

            // 1. The device Windows plays to normally.
            GroupBox sourceGroup = NewGroup("1.  Main output");
            TableLayoutPanel sourceGrid = NewGrid(1);
            sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sourceGrid.Controls.Add(NewNote("Windows keeps playing here exactly as it does now. Dolby Atmos, spatial sound and enhancements are not touched; TwinEars only listens."));
            sourceCombo = new ComboBox();
            sourceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            sourceCombo.Dock = DockStyle.Fill;
            sourceCombo.Margin = new Padding(0, S(6), 0, 0);
            sourceCombo.SelectedIndexChanged += OnSourceChanged;
            sourceGrid.Controls.Add(sourceCombo);
            sourceGroup.Controls.Add(sourceGrid);
            root.Controls.Add(sourceGroup, 0, 2);

            // 2. Devices that get a copy.
            GroupBox targetGroup = NewGroup("2.  Also play on  (tick one or more)");
            targetGroup.AutoSize = false;
            targetList = new ListView();
            targetList.Dock = DockStyle.Fill;
            targetList.View = View.Details;
            targetList.CheckBoxes = true;
            targetList.FullRowSelect = true;
            targetList.HideSelection = false;
            targetList.MultiSelect = false;
            targetList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            targetList.Columns.Add("Device", S(280));
            targetList.Columns.Add("Status", S(200));
            targetList.ItemChecked += OnTargetChecked;
            targetList.SelectedIndexChanged += delegate { BindTune(); };
            targetList.ClientSizeChanged += delegate { FitColumns(); };
            targetGroup.Controls.Add(targetList);
            root.Controls.Add(targetGroup, 0, 3);

            // 3. Per-device delay and volume.
            tuneGroup = NewGroup("3.  Adjust");
            TableLayoutPanel tuneGrid = NewGrid(3);
            tuneGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tuneGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tuneGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            delayBar = NewSlider(MirrorWorker.MaxDelayMs, 100, 5, 50);
            delayBox = NewNumber(MirrorWorker.MaxDelayMs, 5);
            volumeBar = NewSlider(100, 10, 1, 10);
            volumeBox = NewNumber(100, 1);
            tuneGrid.Controls.Add(NewLabel("Delay (ms)"), 0, 0);
            tuneGrid.Controls.Add(delayBar, 1, 0);
            tuneGrid.Controls.Add(delayBox, 2, 0);
            tuneGrid.Controls.Add(NewLabel("Volume (%)"), 0, 1);
            tuneGrid.Controls.Add(volumeBar, 1, 1);
            tuneGrid.Controls.Add(volumeBox, 2, 1);
            Label hint = NewNote("Bluetooth adds lag. If this device sounds EARLIER than the main one, raise Delay until they line up. " +
                                 "If it sounds LATER, swap them: make the laggier (Bluetooth) device the main output.");
            tuneGrid.Controls.Add(hint, 0, 2);
            tuneGrid.SetColumnSpan(hint, 3);
            testButton = new Button();
            testButton.Text = "Play test clicks";
            testButton.AutoSize = true;
            testButton.Margin = new Padding(0, S(8), 0, 0);
            testButton.Click += delegate { ToggleTest(); };
            tips.SetToolTip(testButton, "Plays 10 seconds of clicks through the Windows default output so you can line up the delay by ear.");
            tuneGrid.Controls.Add(testButton, 0, 3);
            tuneGrid.SetColumnSpan(testButton, 3);
            tuneGroup.Controls.Add(tuneGrid);
            root.Controls.Add(tuneGroup, 0, 4);
            delayBar.ValueChanged += OnTuneChanged;
            delayBox.ValueChanged += OnTuneChanged;
            volumeBar.ValueChanged += OnTuneChanged;
            volumeBox.ValueChanged += OnTuneChanged;

            // Options.
            var options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.AutoSize = true;
            options.FlowDirection = FlowDirection.TopDown;
            options.WrapContents = false;
            options.Margin = new Padding(S(2), 0, 0, 0);
            autoOnCheck = NewCheck("Turn mirroring on automatically when TwinEars starts", settings.TurnOnAtStart);
            autoOnCheck.CheckedChanged += delegate { settings.TurnOnAtStart = autoOnCheck.Checked; settingsDirty = true; };
            trayCheck = NewCheck("Keep running in the system tray when this window is closed", settings.CloseToTray);
            trayCheck.CheckedChanged += delegate { settings.CloseToTray = trayCheck.Checked; settingsDirty = true; };
            options.Controls.Add(autoOnCheck);
            options.Controls.Add(trayCheck);
            root.Controls.Add(options, 0, 5);
            UpdateNoteWidths();
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            trayToggleItem = new ToolStripMenuItem("Turn mirroring ON");
            trayToggleItem.Font = new Font(trayToggleItem.Font, FontStyle.Bold);
            trayToggleItem.Click += delegate { SetMirroring(!mirroringOn); };
            menu.Items.Add(trayToggleItem);
            menu.Items.Add("Open TwinEars", null, delegate { ShowFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit TwinEars", null, delegate { ExitApp(); });
            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Icon = trayIconOff;
            tray.Text = "TwinEars";
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            tray.Visible = true;
        }

        TableLayoutPanel NewGrid(int columns)
        {
            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnCount = columns;
            grid.Margin = new Padding(0);
            grid.Padding = new Padding(0);
            return grid;
        }

        GroupBox NewGroup(string text)
        {
            var group = new GroupBox();
            group.Text = text;
            group.Dock = DockStyle.Fill;
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Padding = new Padding(S(10), S(6), S(10), S(10));
            group.Margin = new Padding(0, 0, 0, S(10));
            return group;
        }

        Label NewNote(string text)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            label.ForeColor = SystemColors.GrayText;
            label.Margin = new Padding(0, S(2), 0, 0);
            notes.Add(label);
            return label;
        }

        // Group boxes size themselves from their content's preferred size, which doesn't know about
        // wrapping. Giving wrapped notes an explicit maximum width keeps measured and real heights equal.
        void UpdateNoteWidths()
        {
            int full = ClientSize.Width - S(12) * 2;      // root column
            int inGroup = full - S(10) * 2 - S(12);       // inside a group box
            if (inGroup < S(100)) return;
            foreach (Label note in notes)
            {
                int width = note == warningLabel ? full : inGroup;
                if (note.MaximumSize.Width != width) note.MaximumSize = new Size(width, 0);
            }
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            if (notes.Count > 0) UpdateNoteWidths();
        }

        Label NewLabel(string text)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 0, S(8), 0);
            return label;
        }

        TrackBar NewSlider(int max, int tick, int small, int large)
        {
            var bar = new TrackBar();
            bar.Minimum = 0;
            bar.Maximum = max;
            bar.TickFrequency = tick;
            bar.SmallChange = small;
            bar.LargeChange = large;
            bar.Dock = DockStyle.Fill;
            return bar;
        }

        NumericUpDown NewNumber(int max, int increment)
        {
            var box = new NumericUpDown();
            box.Minimum = 0;
            box.Maximum = max;
            box.Increment = increment;
            box.Width = S(72);
            box.TextAlign = HorizontalAlignment.Right;
            box.Anchor = AnchorStyles.Left;
            box.Margin = new Padding(S(6), 0, 0, 0);
            return box;
        }

        CheckBox NewCheck(string text, bool value)
        {
            var check = new CheckBox();
            check.Text = text;
            check.AutoSize = true;
            check.Checked = value;
            check.Margin = new Padding(0, S(2), 0, S(2));
            return check;
        }

        void FitColumns()
        {
            int width = targetList.ClientSize.Width;
            if (width <= 0 || targetList.Columns.Count < 2) return;
            int first = (int)(width * 0.55);
            targetList.Columns[0].Width = first;
            targetList.Columns[1].Width = Math.Max(S(80), width - first - 1);
        }

        // ---------------------------------------------------------------- behaviour

        void SetMirroring(bool on)
        {
            mirroringOn = on;
            Log.Write("Mirroring " + (on ? "ON" : "OFF"));
            ApplyMirrors();
            UpdateStatus();
        }

        void ApplyMirrors()
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AudioDevice d in devices) present.Add(d.Id);
            manager.Apply(mirroringOn, CurrentSourceId(), present, settings.Targets, settings.BufferMs, settings.SafetyMs);
        }

        void OnTick(object sender, EventArgs e)
        {
            ticks++;
            if (ticks % 3 == 0) RefreshDevices(false);
            ApplyMirrors();   // also restarts failed copies and follows a changed Windows default
            CheckSourceLock();
            UpdateStatus();
            if (settingsDirty && ticks % 2 == 0) SaveSettings();
            if (testPlayer != null && unchecked(Environment.TickCount - testEndTick) > 0) StopTest();
        }

        // When the copies hear nothing but digital silence, check (off the UI thread) whether that's
        // because another app has locked the main output in exclusive mode, so the UI can say so
        // instead of just looking broken.
        void CheckSourceLock()
        {
            string source = CurrentSourceId();
            bool silent = false;
            foreach (MirrorWorker w in manager.Workers)
                if (!w.Ended && w.HearingSilence) silent = true;
            if (!mirroringOn || source == null || !silent)
            {
                lockedSourceId = null;
                return;
            }
            if (probeRunning || unchecked(Environment.TickCount - lastProbeTick) < 15000) return;
            probeRunning = true;
            lastProbeTick = Environment.TickCount;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool locked = false;
                int exclusiveAllowed = -1;
                try
                {
                    locked = Wasapi.IsLockedExclusively(source);
                    if (locked) exclusiveAllowed = Wasapi.GetExclusiveModeAllowed(source);
                }
                catch (Exception) { }
                if (locked != (lockedSourceId != null))
                    Log.Write(locked ? "Main output refuses shared audio (busy); exclusive mode allowed = " + exclusiveAllowed : "Main output accepts audio again");
                lockedExclusiveAllowed = exclusiveAllowed;
                lockedSourceId = locked ? source : null;
                probeRunning = false;
            });
        }

        void RefreshDevices(bool force)
        {
            List<AudioDevice> list;
            string def;
            try
            {
                list = Wasapi.GetActiveOutputs();
                def = Wasapi.GetDefaultOutputId();
            }
            catch (Exception ex)
            {
                Log.Write("Device scan failed: " + ex.Message);
                return;
            }

            var sig = new StringBuilder(def ?? "-");
            foreach (AudioDevice d in list) sig.Append('|').Append(d.Id).Append('=').Append(d.Name);
            string signature = sig.ToString();
            if (!force && signature == deviceSignature) return;
            if (!force && sourceCombo.DroppedDown) return;   // don't yank the list away while it's open

            devices = list;
            defaultId = def;
            deviceSignature = signature;
            foreach (AudioDevice d in devices)
            {
                TargetSetting t = settings.Find(d.Id);
                if (t != null && t.Name != d.Name)
                {
                    t.Name = d.Name;
                    settingsDirty = true;
                }
            }
            Log.Write("Devices: " + devices.Count + " active, default = " + (NameOf(defaultId) ?? "none"));
            RebuildSourceCombo();
            RebuildTargetList();
            ApplyMirrors();
            UpdateStatus();
        }

        void RebuildSourceCombo()
        {
            updating = true;
            try
            {
                sourceCombo.BeginUpdate();
                sourceCombo.Items.Clear();
                sourceCombo.Items.Add(new Choice("", "Windows default output  (now: " + (NameOf(defaultId) ?? "none") + ")"));
                foreach (AudioDevice d in devices) sourceCombo.Items.Add(new Choice(d.Id, d.Name));
                if (settings.SourceId.Length > 0 && NameOf(settings.SourceId) == null)
                {
                    string name = settings.SourceName.Length > 0 ? settings.SourceName : "Saved device";
                    sourceCombo.Items.Add(new Choice(settings.SourceId, name + "  (not connected)"));
                }
                int index = 0;
                for (int i = 0; i < sourceCombo.Items.Count; i++)
                {
                    if (Same(((Choice)sourceCombo.Items[i]).Id, settings.SourceId))
                    {
                        index = i;
                        break;
                    }
                }
                sourceCombo.SelectedIndex = index;
                sourceCombo.EndUpdate();
            }
            finally
            {
                updating = false;
            }
        }

        void OnSourceChanged(object sender, EventArgs e)
        {
            if (updating) return;
            var choice = sourceCombo.SelectedItem as Choice;
            if (choice == null) return;
            settings.SourceId = choice.Id;
            settings.SourceName = choice.Id.Length > 0 ? (NameOf(choice.Id) ?? settings.SourceName) : "";
            settingsDirty = true;
            ApplyMirrors();
            UpdateStatus();
            BindTune();
        }

        void RebuildTargetList()
        {
            string selected = SelectedTargetId();
            updating = true;
            targetList.BeginUpdate();
            try
            {
                targetList.Items.Clear();
                foreach (AudioDevice d in devices) AddTargetItem(d.Id, d.Name);
                // Ticked devices that are switched off/out of range stay listed, so it's clear they'll resume.
                foreach (TargetSetting t in settings.Targets)
                    if (t.Enabled && NameOf(t.Id) == null) AddTargetItem(t.Id, t.Name.Length > 0 ? t.Name : "Saved device");
                foreach (ListViewItem item in targetList.Items)
                {
                    if (Same(item.Tag as string, selected))
                    {
                        item.Selected = true;
                        item.Focused = true;
                    }
                }
            }
            finally
            {
                targetList.EndUpdate();
                updating = false;
            }
            FitColumns();
            BindTune();
        }

        void AddTargetItem(string id, string name)
        {
            TargetSetting t = settings.Find(id);
            var item = new ListViewItem(name);
            item.Tag = id;
            item.UseItemStyleForSubItems = false;
            item.SubItems.Add("");
            item.Checked = t != null && t.Enabled;
            targetList.Items.Add(item);
        }

        void OnTargetChecked(object sender, ItemCheckedEventArgs e)
        {
            if (updating) return;
            string id = e.Item.Tag as string;
            if (id == null) return;
            TargetSetting t = settings.Find(id);
            if (t == null && !e.Item.Checked) return;
            if (t == null) t = settings.GetOrAdd(id, NameOf(id) ?? e.Item.Text);
            if (t.Enabled == e.Item.Checked) return;
            t.Enabled = e.Item.Checked;
            settingsDirty = true;
            if (t.Enabled && !e.Item.Selected) e.Item.Selected = true;   // show its delay and volume
            ApplyMirrors();
            UpdateStatus();
        }

        void BindTune()
        {
            string id = SelectedTargetId();
            bool isMain = Same(id, CurrentSourceId());
            TargetSetting t = settings.Find(id);
            bool wasUpdating = updating;   // may run inside RebuildTargetList via a selection event
            updating = true;
            try
            {
                string name = id == null ? "" : (NameOf(id) ?? (t != null ? t.Name : "")).Replace("&", "&&");
                if (id == null) tuneGroup.Text = "3.  Adjust  (select a device in the list above)";
                else if (isMain) tuneGroup.Text = "3.  Adjust: " + name + "  (main output, nothing to adjust)";
                else tuneGroup.Text = "3.  Adjust: " + name;
                delayBar.Value = Math.Max(0, Math.Min(delayBar.Maximum, t != null ? t.DelayMs : 0));
                delayBox.Value = delayBar.Value;
                volumeBar.Value = Math.Max(0, Math.Min(100, t != null ? t.Volume : 100));
                volumeBox.Value = volumeBar.Value;
                bool editable = id != null && !isMain;
                delayBar.Enabled = editable;
                delayBox.Enabled = editable;
                volumeBar.Enabled = editable;
                volumeBox.Enabled = editable;
            }
            finally
            {
                updating = wasUpdating;
            }
        }

        void OnTuneChanged(object sender, EventArgs e)
        {
            if (updating) return;
            updating = true;
            try
            {
                if (sender == delayBar) delayBox.Value = delayBar.Value;
                else if (sender == delayBox) delayBar.Value = (int)delayBox.Value;
                else if (sender == volumeBar) volumeBox.Value = volumeBar.Value;
                else if (sender == volumeBox) volumeBar.Value = (int)volumeBox.Value;
            }
            finally
            {
                updating = false;
            }

            string id = SelectedTargetId();
            if (id == null) return;
            TargetSetting t = settings.GetOrAdd(id, NameOf(id) ?? "");
            t.DelayMs = delayBar.Value;
            t.Volume = volumeBar.Value;
            MirrorWorker w = manager.Get(id);
            if (w != null)
            {
                w.DelayMs = t.DelayMs;
                w.VolumePercent = t.Volume;
            }
            settingsDirty = true;
        }

        void UpdateStatus()
        {
            string source = CurrentSourceId();
            bool sourcePresent = NameOf(source) != null;
            int active = 0, problems = 0;
            foreach (ListViewItem item in targetList.Items)
            {
                string id = item.Tag as string;
                Color color;
                string text = DescribeTarget(id, source, sourcePresent, out color, ref active, ref problems);
                ListViewItem.ListViewSubItem sub = item.SubItems[1];
                if (sub.Text != text) sub.Text = text;
                if (sub.ForeColor != color) sub.ForeColor = color;
                Color nameColor = Same(id, source) || NameOf(id) == null ? SystemColors.GrayText : SystemColors.WindowText;
                if (item.ForeColor != nameColor) item.ForeColor = nameColor;
            }

            string summary;
            if (!mirroringOn) summary = "Off. Nothing is being copied, so your audio works exactly as normal.";
            else if (!sourcePresent) summary = "On, waiting for the main output to connect.";
            else if (active == 0) summary = "On, but there's nothing to copy to yet: tick a device under \"Also play on\".";
            else summary = "On: copying " + NameOf(source) + " to " + active + (active == 1 ? " device" : " devices") +
                           (problems > 0 ? " (" + problems + " with a problem, retrying)" : "") + ".";
            SetText(statusLabel, summary);

            string warning = null;
            if (mirroringOn && sourcePresent && Same(lockedSourceId, source))
            {
                string name = NameOf(source);
                if (lockedExclusiveAllowed == 0)
                {
                    // Exclusive mode is already off, so it isn't another app: the device or its driver is refusing.
                    warning = "Windows won't play anything on " + name + " right now: it reports the device as busy, even though " +
                              "exclusive mode is off. No app can play there, not just TwinEars. This is the device or its driver: " +
                              "if it's a headphone jack, make sure something is plugged in, or unplug and replug it; otherwise a " +
                              "restart usually clears it. Until then, pick a different Main output in step 1.";
                }
                else
                {
                    warning = "Windows won't play anything on " + name + " right now: it reports the device as busy, usually because " +
                              "another app has taken exclusive control of it. Close that app, or untick \"Allow applications to take " +
                              "exclusive control\" for this device (Sound control panel > Properties > Advanced), then unplug and " +
                              "replug it or pick a different Main output in step 1. Until then the copies stay silent.";
                }
            }
            if (warning != null) SetText(warningLabel, warning);
            if (warningLabel.Visible != (warning != null)) warningLabel.Visible = warning != null;

            SetText(toggleButton, mirroringOn ? "Mirroring: ON" : "Mirroring: OFF");
            Color back = mirroringOn ? OnColor : OffColor;
            if (toggleButton.BackColor != back)
            {
                toggleButton.BackColor = back;
                toggleButton.FlatAppearance.MouseOverBackColor = Blend(back, Color.White, 0.12f);
                toggleButton.FlatAppearance.MouseDownBackColor = Blend(back, Color.Black, 0.12f);
            }
            Icon icon = mirroringOn ? windowIconOn : windowIconOff;
            if (Icon != icon) Icon = icon;

            if (tray != null)
            {
                Icon trayIcon = mirroringOn ? trayIconOn : trayIconOff;
                if (tray.Icon != trayIcon) tray.Icon = trayIcon;
                string tip = "TwinEars: " + (mirroringOn ? "ON, copying to " + active + (active == 1 ? " device" : " devices") : "OFF");
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                if (tray.Text != tip) tray.Text = tip;
                trayToggleItem.Text = mirroringOn ? "Turn mirroring OFF" : "Turn mirroring ON";
            }
        }

        string DescribeTarget(string id, string source, bool sourcePresent, out Color color, ref int active, ref int problems)
        {
            color = SystemColors.GrayText;
            TargetSetting t = settings.Find(id);
            bool ticked = t != null && t.Enabled;
            if (Same(id, source)) return ticked ? "Main output: already plays here" : "Main output";
            if (NameOf(id) == null) return "Not connected (resumes when it's back)";
            if (!ticked) return "";
            active++;
            if (!mirroringOn) return "Ready (mirroring is off)";
            if (!sourcePresent)
            {
                color = WarnText;
                return "Waiting for the main output";
            }
            MirrorWorker w = manager.Get(id);
            if (w == null) return "Starting...";
            if (w.Ended)
            {
                problems++;
                color = BadText;
                return "Problem: " + (w.Error ?? "stopped") + ". Retrying...";
            }
            if (w.State == MirrorState.Starting) return "Starting...";
            if (Same(lockedSourceId, source))
            {
                color = WarnText;
                return "Silent: Windows reports the main output as busy";
            }
            if (w.State == MirrorState.Playing)
            {
                color = GoodText;
                return "Playing";
            }
            return "On, waiting for sound";
        }

        // ---------------------------------------------------------------- test clicks

        void ToggleTest()
        {
            if (testPlayer != null)
            {
                StopTest();
                return;
            }
            try
            {
                testPlayer = new SoundPlayer(new MemoryStream(TestClicks.Create()));
                testPlayer.Play();
                testEndTick = Environment.TickCount + TestClicks.Seconds * 1000 + 500;
                testButton.Text = "Stop test clicks";
            }
            catch (Exception ex)
            {
                testPlayer = null;
                MessageBox.Show(this, "Couldn't play the test sound: " + ex.Message, "TwinEars", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void StopTest()
        {
            if (testPlayer == null) return;
            try
            {
                testPlayer.Stop();
                testPlayer.Dispose();
            }
            catch (Exception) { }
            testPlayer = null;
            testButton.Text = "Play test clicks";
        }

        // ---------------------------------------------------------------- window / tray lifecycle

        // Commands forwarded from a second copy of TwinEars (see Program). Arrives on a thread-pool thread.
        public void RunCommand(string command)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(RunCommand), command); }
                catch (Exception) { }   // window already closing
                return;
            }
            if (shutDown) return;
            Log.Write("Command: " + command);
            switch (command)
            {
                case "on": SetMirroring(true); break;
                case "off": SetMirroring(false); break;
                case "toggle": SetMirroring(!mirroringOn); break;
                case "exit": ExitApp(); break;
                default: ShowFromTray(); break;
            }
        }

        public void ShowFromTray()
        {
            if (shutDown) return;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                // Started with /tray: create the window but keep it hidden until opened from the tray.
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && settings.CloseToTray && tray != null) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && tray != null && settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(4000, "TwinEars is still running",
                        "Mirroring is " + (mirroringOn ? "ON" : "OFF") + ". Right-click this icon to turn it off or exit.", ToolTipIcon.Info);
                }
                return;
            }
            Shutdown();
            base.OnFormClosing(e);
        }

        void Shutdown()
        {
            if (shutDown) return;
            shutDown = true;
            timer.Stop();
            StopTest();
            manager.StopAll();   // closes every stream; Windows audio is exactly as it was
            if (settingsDirty) SaveSettings();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            Log.Write("Exit");
        }

        void SaveSettings()
        {
            settingsDirty = false;
            if (!settings.Save()) Log.Write("Could not save settings");
        }

        // ---------------------------------------------------------------- helpers

        string CurrentSourceId()
        {
            return settings.SourceId.Length > 0 ? settings.SourceId : defaultId;
        }

        string SelectedTargetId()
        {
            return targetList.SelectedItems.Count > 0 ? targetList.SelectedItems[0].Tag as string : null;
        }

        string NameOf(string id)
        {
            if (id == null) return null;
            foreach (AudioDevice d in devices)
                if (Same(d.Id, id)) return d.Name;
            return null;
        }

        static bool Same(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static void SetText(Control control, string text)
        {
            if (control.Text != text) control.Text = text;
        }

        static Color Blend(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
        }

        sealed class Choice
        {
            public readonly string Id;
            readonly string text;

            public Choice(string id, string text)
            {
                Id = id;
                this.text = text;
            }

            public override string ToString()
            {
                return text;
            }
        }
    }
}
