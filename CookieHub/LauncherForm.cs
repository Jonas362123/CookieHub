using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using ICSharpCode.SharpZipLib.Zip;
using System.Data.SqlClient;
using System.Text;
using System.Management;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CookieHub {
    public partial class Application : Form {

        private DownloadProgressTracker _downloadProgressTracker;
        private WebClient _webClient;
        private SqlConnection con;

        public Version LocalVersion { get { return new Version(Properties.Settings.Default.VersionText); } }
        public Version OnlineVersion { get; private set; }

        private List<PatchNoteBlock> patchNoteBlocks = new List<PatchNoteBlock>();

        private string hardwareId;

        private bool _isReady;
        public bool IsReady {
            get {
                return _isReady;
            }
            set {
                _isReady = value;
                TogglePlayButton(value);
                InitializeFooter();
            }
        }

        public bool UpToDate { get { return LocalVersion >= OnlineVersion; } }

        public Application() {
            InitializeComponent();
            int style = NativeWinAPI.GetWindowLong(this.Handle, NativeWinAPI.GWL_EXSTYLE);
            style |= NativeWinAPI.WS_EX_COMPOSITED;
            NativeWinAPI.SetWindowLong(this.Handle, NativeWinAPI.GWL_EXSTYLE, style);
        }

        private void OnLoadApplication(object sender, EventArgs e) {
            InitializeConstantsSettings();
            InitializeFiles();
            InitializeImages();
            FetchPatchNotes();
            InitializeVersionControl();
            con = new SqlConnection(Constants.SQLCONNECTIONSTRING);
            Identification();
            Authentication();

            IsReady = UpToDate;

            _downloadProgressTracker = new DownloadProgressTracker(50, TimeSpan.FromMilliseconds(500));

            if (!UpToDate && Constants.AUTOMATICALLY_BEGIN_UPDATING) {
                DownloadFile();
            }
        }

        private void Identification() {
            hardwareId = libc.hwid.HwId.Generate();
        }

        private void Authentication() {
            con.Open();
            string query = "SELECT * FROM users WHERE hwid=@hwid";
            SqlCommand cmd = new SqlCommand(query, con);
            cmd.Parameters.AddWithValue("@hwid", hardwareId);
            List<string> names = new List<string>();
            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    names.Add(reader[1].ToString());
                }
            }
            con.Close();

            if (names.Count == 0) {
                show_NameBox();
            } else if (names.Count == 1) {
                labelUser.Text = names[0];
                labelUser.Visible = true;
                Authorization();

            } else
                MessageBox.Show("ID Overlap", "Fatal error");
        }

        private void Authorization() {

        }

        private void InitializeConstantsSettings() {
            Name = Constants.GAME_TITLE;
            Text = Constants.LAUNCHER_NAME;
            SetUpButtonEvents();

            currentVersionLabel.Visible = Constants.SHOW_VERSION_TEXT;

        }

        private void InitializeFiles() {
            if (!Directory.Exists(Constants.DESTINATION_PATH)) {
                Directory.CreateDirectory(Constants.DESTINATION_PATH);
            } 
        }

        private void InitializeImages() {
            LoadApplicationIcon();
            navbarPanel.BackColor = Color.FromArgb(25, 100, 100, 100); // // Make panel background semi transparent
            logoPictureBox.SizeMode = PictureBoxSizeMode.CenterImage;
            closePictureBox.SizeMode = PictureBoxSizeMode.CenterImage; // Center the X icon
            minimizePictureBox.SizeMode = PictureBoxSizeMode.CenterImage; // Center the - icon
            try {
                logoPictureBox.Load(Constants.LOGO_URL);
                using(WebClient webClient = new WebClient()) {
                    using (Stream stream = webClient.OpenRead(Constants.BACKGROUND_URL)) {
                        BackgroundImage = Image.FromStream(stream);
                    }
                }
            } catch (Exception e) {
                MessageBox.Show("The launcher was unable to retrieve some game images from the server! " + e, "Error");
            }
        }

        private void LoadApplicationIcon() {
            WebRequest request = (HttpWebRequest)WebRequest.Create(Constants.APPLICATION_ICON_URL);

            Bitmap bm = new Bitmap(32,32); 
            MemoryStream memStream;

            using (Stream response = request.GetResponse().GetResponseStream()) {
                memStream = new MemoryStream();
                byte[] buffer = new byte[1024];
                int byteCount;

                do {
                    byteCount = response.Read(buffer, 0, buffer.Length);
                    memStream.Write(buffer, 0, byteCount);
                } while (byteCount > 0);
            }

            bm = new Bitmap(Image.FromStream(memStream));                 

            if (bm != null) {
                Icon = Icon.FromHandle(bm.GetHicon());
            }

        }

        private void InitializeVersionControl() {
            currentVersionLabel.Text = Properties.Settings.Default.VersionText;
            OnlineVersion = GetOnlineVersion();

            Console.WriteLine("We are on version " + LocalVersion + " and the online version is " + OnlineVersion);
        }

        private void InitializeFooter() {
            if (IsReady) {
                updateProgressBar.Visible = false;
                clientReadyLabel.Visible = true;
            } else {
                updateProgressBar.Visible = true;
                clientReadyLabel.Visible= false;
            }
        }

        private Version GetOnlineVersion() {
            try {
                string onlineVersion = new WebClient().DownloadString(Constants.VERSION_URL);
                Console.WriteLine(LocalVersion >= new Version(onlineVersion));
                Version.TryParse(onlineVersion, out Version result);
                return result;
            } catch {
                MessageBox.Show("The launcher was unable to read the current client version from the server!", "Fatal error");
                return null;
            }
        }

        private void OnClickPlay(object sender, EventArgs e) {
            if (IsReady) {
                LaunchGame();
            } else {
                DownloadFile();
            }
        }

        private void DownloadFile() {
            using (_webClient = new WebClient()) { 
                _webClient.DownloadProgressChanged += OnDownloadProgressChanged;
                _webClient.DownloadFileCompleted += new AsyncCompletedEventHandler(OnDownloadCompleted);
                _webClient.DownloadFileAsync(new Uri(Constants.CLIENT_DOWNLOAD_URL), Constants.ZIP_PATH);
            }
            
        }

        private void OnDownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e) {
            _downloadProgressTracker.SetProgress(e.BytesReceived, e.TotalBytesToReceive);
            updateProgressBar.Value = e.ProgressPercentage;
            updateLabelText.Text = string.Format("Downloading: {0} of {1} @ {2}", StringUtility.FormatBytes(e.BytesReceived),
                StringUtility.FormatBytes(e.TotalBytesToReceive), _downloadProgressTracker.GetBytesPerSecondString());

        }

        private void OnDownloadCompleted(object sender, AsyncCompletedEventArgs e) {
            _downloadProgressTracker.Reset();
            updateLabelText.Text = "Download finished - extracting...";

            Extract extract = new Extract(this);
            extract.Run();
        }

        public void SetLauncherReady() {
            updateLabelText.Text = "";
            if (!File.Exists(Constants.GAME_EXECUTABLE_PATH)) {
                MessageBox.Show("Couldn't make a connection to the game server. Please try again later or inform the developer if the issue persists.", "Fatal error");
                return;
            }

            currentVersionLabel.Text = OnlineVersion.ToString();
            Properties.Settings.Default.VersionText = OnlineVersion.ToString();
            Properties.Settings.Default.Save();
            Console.WriteLine("Updated version. Now running on version: " + LocalVersion);
            IsReady = true;

            if (Constants.AUTOMATICALLY_LAUNCH_GAME_AFTER_UPDATING) 
                LaunchGame();

            try {
                File.Delete(Constants.ZIP_PATH);
            } catch {
                MessageBox.Show("Couldn't delete the downloaded zip file after extraction.");
            }
        }

        private void FetchPatchNotes() {
            try {
                XmlDocument doc = new XmlDocument();
                doc.Load(Constants.PATCH_NOTES_URL);

               foreach(XmlNode node in doc.DocumentElement) {
                    PatchNoteBlock block = new PatchNoteBlock();
                    for(int i = 0; i < node.ChildNodes.Count; i++) {
                        switch(i) {
                            case 0:
                                block.Title = node.ChildNodes[i].InnerText;
                                break;
                            case 1:
                                block.Text = node.ChildNodes[i].InnerText;
                                break;
                            case 2:
                                block.Link = node.ChildNodes[i].InnerText;
                                break;
                        }
                    }
                    patchNoteBlocks.Add(block);
                }
            } catch {
                patchContainerPanel.Visible = false;
                if (Constants.SHOW_ERROR_BOX_IF_PATCH_NOTES_DOWNLOAD_FAILS)
                    MessageBox.Show("The launcher was unable to retrieve patch notes from the server!");
            }

            Label[] patchTitleObjects = { patchTitle1 };
            Label[] patchTextObjects = { patchText1 };

            for(int i = 0; i < patchNoteBlocks.Count; i++) {
                patchTitleObjects[i].Text = patchNoteBlocks[i].Title;
                patchTextObjects[i].Text = patchNoteBlocks[i].Text;
            }
        }

        private void LaunchGame() {
            try {
                Process.Start(Constants.GAME_EXECUTABLE_PATH);
                Environment.Exit(0);
            } catch {
                IsReady = false;
                DownloadFile();
                MessageBox.Show("Couldn't locate the game executable! Attempting to redownload - please wait.", "Fatal Error");
            }
        }

        private void TogglePlayButton(bool toggle) {
            switch(toggle) {
                case true:
                    playButton.BackColor = Color.Green;
                    playButton.Text = "Play";
                    break;
                case false:
                    playButton.BackColor = Color.DeepSkyBlue;
                    playButton.Text = "Update";
                    break;
            }
        }
        
        // Move the form with LMB
        private void Application_MouseDown(object sender, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                NativeWinAPI.ReleaseCapture();
                NativeWinAPI.SendMessage(Handle, NativeWinAPI.WM_NCLBUTTONDOWN, NativeWinAPI.HT_CAPTION, 0);
            }
        }
        
        private void SetUpButtonEvents() {
            Button[] buttons = { navbarButton1, navbarButton2, navbarButton3, navbarButton4, navbarButton5 };

            for(int i = 0; i < buttons.Length; i++) {
                buttons[i].Click += new EventHandler(OnClickButton);
                buttons[i].Text = Constants.NAVBAR_BUTTON_TEXT_ARRAY[i];
            }
        }

        public void OnClickButton(object sender, EventArgs e) {
            Button button = (Button) sender;
            switch(button.Name) {
                case nameof(navbarButton1):
                    System.Diagnostics.Process.Start(Constants.NAVBAR_BUTTON_1_URL);
                    break;
                case nameof(navbarButton2):
                    
                    break;
                case nameof(navbarButton3):
                    
                    break;
                case nameof(navbarButton4):
                    
                    break;
                case nameof(navbarButton5):
                    System.Diagnostics.Process.Start(Constants.NAVBAR_BUTTON_5_URL);
                    break;

                case nameof(patchButton1):
                    Process.Start(patchNoteBlocks[0].Link);
                    break;
            }
        }

        private void OnMouseEnterIcon(object sender, EventArgs e) {
            var pictureBox = (PictureBox) sender;
            pictureBox.BackColor = Color.FromArgb(50, 255, 255, 255);
        }

        private void OnMouseLeaveIcon(object sender, EventArgs e) {
            var pictureBox = (PictureBox) sender;
            pictureBox.BackColor = Color.FromArgb(0, 255, 255, 255);
        }

        private void minimizePictureBox_Click(object sender, EventArgs e) {
            WindowState = FormWindowState.Minimized;
        }

        private void closePictureBox_Click(object sender, EventArgs e) {
            Environment.Exit(0);
        }

        private void currentVersionLabel_Click(object sender, EventArgs e)
        {

        }

        private void navbarButton1_Click(object sender, EventArgs e)
        {

        }

        private void buttonOptions_Click(object sender, EventArgs e)
        {
            if (TextBox.Visible)
                hide_NameBox();
            else
                show_NameBox();
        }

        private void show_NameBox()
        {
            TextBox.Visible = true;
            label_name.Visible = true;
            textBox_name.Visible = true;
            button_name.Visible = true;
        }
        private void hide_NameBox()
        {
            TextBox.Visible = false;
            label_name.Visible = false;
            textBox_name.Visible = false;
            button_name.Visible = false;
        }

        private void button_name_Click(object sender, EventArgs e)
        {
            if (textBox_name.TextLength < 4)
            {
                MessageBox.Show("Name too short!", "Fatal error");
            } 
            else
            {
                button_name.Visible = false;

                string strName = textBox_name.Text;

                con.Open();
                string query = "SELECT COUNT(*) FROM users WHERE name=@name";
                SqlCommand cmd = new SqlCommand(query, con);
                cmd.Parameters.AddWithValue("@name", strName);

                int Count = (int)cmd.ExecuteScalar();

                if (Count == 0)
                {
                    string query1 = "INSERT INTO users (name, hwid) VALUES (@name, @hwid)";
                    SqlCommand cmd1 = new SqlCommand(query1, con);
                    cmd1.Parameters.AddWithValue("@name", strName);
                    cmd1.Parameters.AddWithValue("@hwid", hardwareId);
                    if (cmd1.ExecuteNonQuery() == 0)
                    {
                        MessageBox.Show("Failed to register user", "Fatal error");
                        this.Close();
                    }
                    string query2 = "INSERT INTO perm (hwid, perms) VALUES (@hwid, @perms)";
                    SqlCommand cmd2 = new SqlCommand(query2, con);
                    cmd2.Parameters.AddWithValue("@hwid", hardwareId);
                    cmd2.Parameters.AddWithValue("@perms", "00000000000000000000");
                    if (cmd2.ExecuteNonQuery() == 0)
                    {
                        MessageBox.Show("Failed to register user", "Fatal error");
                        this.Close();
                    }
                    labelUser.Text = strName;
                    labelUser.Visible = true;
                    hide_NameBox();
                }
                else
                {
                    MessageBox.Show("Name not available!", "Fatal error");
                    show_NameBox();
                }
                con.Close();


            }
        }
    }
}
