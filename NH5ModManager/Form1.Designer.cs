namespace NH5ModManager
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            btnBrowse = new Button();
            txtGamePath = new TextBox();
            btnRefresh = new Button();
            btnDeploy = new Button();
            lstMods = new ListView();
            columnHeader1 = new ColumnHeader();
            statusStrip1 = new StatusStrip();
            lblStatus = new ToolStripStatusLabel();
            lblProfile = new Label();
            cmbProfiles = new ComboBox();
            btnSaveProfile = new Button();
            chkUnlockDLC = new CheckBox();
            statusStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // btnBrowse
            // 
            btnBrowse.Location = new Point(12, 12);
            btnBrowse.Name = "btnBrowse";
            btnBrowse.Size = new Size(80, 23);
            btnBrowse.TabIndex = 0;
            btnBrowse.Text = "Browse";
            btnBrowse.UseVisualStyleBackColor = true;
            btnBrowse.Click += btnBrowse_Click;
            // 
            // txtGamePath
            // 
            txtGamePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtGamePath.Location = new Point(98, 12);
            txtGamePath.Name = "txtGamePath";
            txtGamePath.ReadOnly = true;
            txtGamePath.Size = new Size(494, 23);
            txtGamePath.TabIndex = 1;
            // 
            // btnRefresh
            // 
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Location = new Point(600, 12);
            btnRefresh.Name = "btnRefresh";
            btnRefresh.Size = new Size(80, 23);
            btnRefresh.TabIndex = 5;
            btnRefresh.Text = "Refresh";
            btnRefresh.UseVisualStyleBackColor = true;
            btnRefresh.Click += btnRefresh_Click;
            // 
            // btnDeploy
            // 
            btnDeploy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDeploy.Location = new Point(688, 12);
            btnDeploy.Name = "btnDeploy";
            btnDeploy.Size = new Size(100, 23);
            btnDeploy.TabIndex = 4;
            btnDeploy.Text = "Deploy Mods";
            btnDeploy.UseVisualStyleBackColor = true;
            btnDeploy.Click += btnDeploy_Click;
            // 
            // lstMods
            // 
            lstMods.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lstMods.CheckBoxes = true;
            lstMods.Columns.AddRange(new ColumnHeader[] { columnHeader1 });
            lstMods.FullRowSelect = true;
            lstMods.GridLines = true;
            lstMods.Location = new Point(12, 75);
            lstMods.Name = "lstMods";
            lstMods.Size = new Size(776, 455);
            lstMods.TabIndex = 2;
            lstMods.UseCompatibleStateImageBehavior = false;
            lstMods.View = View.Details;
            // 
            // columnHeader1
            // 
            columnHeader1.Text = "Installed Mod File";
            columnHeader1.Width = 750;
            // 
            // statusStrip1
            // 
            statusStrip1.Items.AddRange(new ToolStripItem[] { lblStatus });
            statusStrip1.Location = new Point(0, 538);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(800, 22);
            statusStrip1.TabIndex = 3;
            statusStrip1.Text = "statusStrip1";
            // 
            // lblStatus
            // 
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(108, 17);
            lblStatus.Text = "Status: Initializing...";
            // 
            // lblProfile
            // 
            lblProfile.AutoSize = true;
            lblProfile.Location = new Point(12, 45);
            lblProfile.Name = "lblProfile";
            lblProfile.Size = new Size(72, 15);
            lblProfile.TabIndex = 6;
            lblProfile.Text = "Mod Profile:";
            // 
            // cmbProfiles
            // 
            cmbProfiles.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbProfiles.FormattingEnabled = true;
            cmbProfiles.Location = new Point(89, 42);
            cmbProfiles.Name = "cmbProfiles";
            cmbProfiles.Size = new Size(200, 23);
            cmbProfiles.TabIndex = 7;
            // 
            // btnSaveProfile
            // 
            btnSaveProfile.Location = new Point(295, 41);
            btnSaveProfile.Name = "btnSaveProfile";
            btnSaveProfile.Size = new Size(95, 25);
            btnSaveProfile.TabIndex = 8;
            btnSaveProfile.Text = "+ New Profile";
            btnSaveProfile.UseVisualStyleBackColor = true;
            btnSaveProfile.Click += btnSaveProfile_Click;
            // 
            // chkUnlockDLC
            // 
            chkUnlockDLC.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkUnlockDLC.AutoSize = true;
            chkUnlockDLC.Location = new Point(664, 44);
            chkUnlockDLC.Name = "chkUnlockDLC";
            chkUnlockDLC.Size = new Size(124, 19);
            chkUnlockDLC.TabIndex = 9;
            chkUnlockDLC.Text = "Auto-Unlock DLCs";
            chkUnlockDLC.UseVisualStyleBackColor = true;
            // 
            // Form1
            // 
            AllowDrop = true;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 560);
            Controls.Add(chkUnlockDLC);
            Controls.Add(btnSaveProfile);
            Controls.Add(cmbProfiles);
            Controls.Add(lblProfile);
            Controls.Add(lstMods);
            Controls.Add(btnDeploy);
            Controls.Add(btnRefresh);
            Controls.Add(txtGamePath);
            Controls.Add(btnBrowse);
            Controls.Add(statusStrip1);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "NASCAR Heat 5 Mod Manager";
            Load += Form1_Load;
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button btnBrowse;
        private TextBox txtGamePath;
        private Button btnRefresh;
        private Button btnDeploy;
        private ListView lstMods;
        private ColumnHeader columnHeader1;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel lblStatus;
        private Label lblProfile;
        private ComboBox cmbProfiles;
        private Button btnSaveProfile;
        private CheckBox chkUnlockDLC;
    }
}