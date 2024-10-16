﻿using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using DynamicDevices.DiskWriter.Detection;
using DynamicDevices.DiskWriter.Win32;
using Microsoft.Win32;
using System.Management;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Drawing;
using System.Deployment.Application;

namespace DynamicDevices.DiskWriter
{
    public partial class MainForm : Form
    {
        #region Fields

        private readonly Disk _disk;
        private readonly IDiskAccess _diskAccess;
        private  DriveDetector _watcher = new DriveDetector();

        private EnumCompressionType _eCompType;

        #endregion
        enum SdStates
        {
            INIT=0,
            WAIT_INSERT,
            INSERTED,
            NOT_EMPTY,
            WAIT_WRITE,
            WRITING,
            WRITING_DONE_PASS,
            WRITING_DONE_FAIL,
            WAIT_REMOVE,
            REMOVED
        }
        
        DispatcherTimer dispatcherTimer = null;
        const int TIMER_EVENT_SEC = 1;
        SdStates sdcardStatus = SdStates.INIT;
        
        bool waitAutoWrite=false;
        
        int waitAutoWriteSeconds = 0;
        const int WAIT_FOR_WRITE_SEC = 5;

        string textCheckBoxLock = "Check to lock drive letter";
        
        #region Constructor

        public MainForm()
        {
            InitializeComponent();

            dispatcherTimer = new DispatcherTimer();
            dispatcherTimer.Tick += dispatcherTimer_Tick;
            dispatcherTimer.Interval = new TimeSpan(0, 0, TIMER_EVENT_SEC);
            dispatcherTimer.Start();
            
            checkBoxLock.Checked = false;
            checkBoxLock.Text = textCheckBoxLock;
            checkBoxUseMBR.Checked = true;

            MessageBoxEx.Owner = this.Handle;

            toolStripStatusLabel1.Text = @"Initialised. Licensed under GPLv3.";

            saveFileDialog1.OverwritePrompt = false;
            saveFileDialog1.Filter = @"Image Files (*.img,*.bin,*.sdcard)|*.img;*.bin;*.sdcard|Compressed Files (*.zip,*.gz,*tgz)|*.zip;*.gz;*.tgz|All files (*.*)|*.*";

            // Set version into title
            //var version = Assembly.GetEntryAssembly().GetName().Version;
            string version = Application.ProductVersion;
            Text += @" v" + version;
            

            // Set app icon (not working on Mono/Linux)
            if (Environment.OSVersion.Platform != PlatformID.Unix)
                Icon = Utility.GetAppIcon();

            PopulateDrives();
            if (comboBoxDrives.Items.Count > 0)
                EnableButtons();
            else
                DisableButtons(false);

            // Read registry values
            var key = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Dynamic Devices Ltd\\DiskImager");
            if (key != null)
            {
                var file = (string)key.GetValue("FileName", "");
                if (File.Exists(file))
                {
                    //textBoxFileName.Text = file;
                }

                var drive = (string)key.GetValue("Drive", "");
                if (string.IsNullOrEmpty(drive))
                {
                    foreach(var cbDrive in comboBoxDrives.Items)
                    {
                        if((string) cbDrive == drive)
                        {
                            comboBoxDrives.SelectedItem = cbDrive;
                        }
                    }
                }

                Globals.CompressionLevel = (int)key.GetValue("CompressionLevel", Globals.CompressionLevel);
                Globals.MaxBufferSize = (int)key.GetValue("MaxBufferSize", Globals.MaxBufferSize);

                key.Close();
            }

            // Create disk object for media accesses
            var pid = Environment.OSVersion.Platform;
            if (pid == PlatformID.Unix)
                _diskAccess = new LinuxDiskAccess();
            else 
                _diskAccess = new Win32DiskAccess();

            _disk = new Disk(_diskAccess);

            _disk.OnLogMsg += _disk_OnLogMsg;
            _disk.OnProgress += _disk_OnProgress;
            
            // Detect insertions / removals
            _watcher.DeviceArrived += OnDriveArrived;
            _watcher.DeviceRemoved += OnDriveRemoved;
            StartListenForChanges();
        }

        #endregion


        private void dispatcherTimer_Tick(object sender, EventArgs e)
        {
            if (sdcardStatus == SdStates.WRITING_DONE_PASS)
            {
                sdcardStatus= SdStates.WAIT_REMOVE;
                labelSdStatus.ForeColor = Color.Black;
                labelSdStatus.Text = "Remove SDCARD";
                if (checkBoxLock.Checked)
                {
                    waitAutoWrite = true;
                }
            }
            // code goes here
            DisplayAllDrivesToolStripMenuItemCheckedChanged(null, null);
            /*
            if (sdcardStatus == SdStates.INIT)
            {
                labelSdStatus.ForeColor = Color.Black;
                labelSdStatus.Text = "Insert NEW SDCARD";
            }
            else
            */
            if ((sdcardStatus == SdStates.REMOVED))//|| (sdcardStatus == SdStates.INIT))
            {
                sdcardStatus = SdStates.WAIT_INSERT;
                labelSdStatus.ForeColor = Color.Black;
                labelSdStatus.Text = "Insert NEW SDCARD";
            }
            else if (sdcardStatus == SdStates.INSERTED)
            {
                
                //sdcardStatus = SdStates.WAIT_INSERT;
                labelSdStatus.ForeColor = Color.Black;
                labelSdStatus.Text = "SDCARD IS READY";
                if ((checkBoxLock.Checked)&&(!buttonWrite.Enabled))
                {
                    if (waitAutoWrite) {
                        waitAutoWriteSeconds++;
                        if (waitAutoWriteSeconds > WAIT_FOR_WRITE_SEC)
                        {
                            waitAutoWrite=false;
                            waitAutoWriteSeconds = 0;
                            ButtonWriteClick(null,null);  
                        }
                        else
                        {
                            labelSdStatus.Text += ". START WRITING AFTER " + (WAIT_FOR_WRITE_SEC - waitAutoWriteSeconds).ToString()+" sec";
                        }
                    }
                    
                }
                
            }
            else if (sdcardStatus == SdStates.NOT_EMPTY)
            {

                sdcardStatus = SdStates.WAIT_REMOVE;
                labelSdStatus.ForeColor = Color.Red;
                labelSdStatus.Text = "SDCARD IS NOT EMPTY. Remove and Insert NEW SDCARD";
            }

        }
        public override sealed string Text
        {
            get { return base.Text; }
            set { base.Text = value; }
        }

        #region Disk access event handlers

        /// <summary>
        /// Called to update progress bar as we read/write disk
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="progressPercentage"></param>
        void _disk_OnProgress(object sender, int progressPercentage)
        {
            progressBar1.Value = progressPercentage;
            Application.DoEvents();
        }

        /// <summary>
        /// Called to display/log messages from disk handling
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        void _disk_OnLogMsg(object sender, string message)
        {
            toolStripStatusLabel1.Text = message;
            Application.DoEvents();
        }

        #endregion

        #region UI Handling

        /// <summary>
        /// Close the application
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonExitClick(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Select a file for read/write from/to removable media
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonChooseFileClick(object sender, EventArgs e)
        {
            ChooseFile();
        }

        /// <summary>
        /// Read from removable media to file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonReadClick(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedIndex < 0)
                return;

            var drive = (string)comboBoxDrives.SelectedItem;

            if(string.IsNullOrEmpty(textBoxFileName.Text))
                ChooseFile();

            DisableButtons(true);

            try
            {
                var start = 0l;

                if(!string.IsNullOrEmpty(textBoxStart.Text))
                {
                    try
                    {
                        start = long.Parse(textBoxStart.Text);
                    } catch
                    {
                    }
                }

                var length = 0l;
                if(!string.IsNullOrEmpty(textBoxLength.Text))
                {
                    try
                    {
                        length = long.Parse(textBoxLength.Text);
                    } catch
                    {
                    }
                }

                _disk.ReadDrive(drive, textBoxFileName.Text, _eCompType, checkBoxUseMBR.Checked, start, length);
            } catch(Exception ex)
            {
                toolStripStatusLabel1.Text = ex.Message;
            }

            EnableButtons();
        }

        /// <summary>
        /// Write to removable media from file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonWriteClick(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedIndex < 0)
                return;
            
            var drive = (string)comboBoxDrives.SelectedItem;

            if (string.IsNullOrEmpty(textBoxFileName.Text))
                ChooseFile();

            if( ((string)comboBoxDrives.SelectedItem).ToUpper().StartsWith("C:"))
            {
                var dr =
                    MessageBox.Show(
                        "C: is almost certainly your main hard drive. Writing to this will likely destroy your data, and brick your PC. Are you absolutely sure you want to do this?",
                        "*** WARNING ***", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes)
                    return;
            }

            DisableButtons(true);

            if(!File.Exists(textBoxFileName.Text))
            {
                MessageBoxEx.Show("File does not exist!", "I/O Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                EnableButtons();
                return;
            }

            sdcardStatus = SdStates.WRITING;
            labelSdStatus.ForeColor = Color.Black;
            labelSdStatus.Text = "WRITING";

            var success = false;
            try
            {
                success = _disk.WriteDrive(drive, textBoxFileName.Text, _eCompType);
            }
            catch (Exception ex)
            {
                success = false;
                toolStripStatusLabel1.Text = ex.Message;
            }

            if (!success && !_disk.IsCancelling)
            {
                labelSdStatus.Text = "SDCARD WRITE FAIL. REWRITE or REPLACE SD.";
                labelSdStatus.ForeColor = Color.Red;
                sdcardStatus = SdStates.WRITING_DONE_FAIL;
                MessageBoxEx.Show("Problem writing to disk. Is it write-protected?", "Write Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                EnableButtons();
            }
            else
            {
                labelSdStatus.Text = "SDCARD WRITE DONE. INSERT NEW SDCARD.";
                labelSdStatus.ForeColor = Color.Green;
                sdcardStatus = SdStates.WRITING_DONE_PASS;
                
            }
            
        }

        private void ButtonEraseMBRClick(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedIndex < 0)
                return;

            var drive = (string)comboBoxDrives.SelectedItem;

            var success = false;
            try
            {
                success = _disk.EraseMBR(drive);
            }
            catch (Exception ex)
            {
                success = false;
                toolStripStatusLabel1.Text = ex.Message;
            }

            if (!success && !_disk.IsCancelling)
                MessageBoxEx.Show("Problem writing to disk. Is it write-protected?", "Write Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                MessageBoxEx.Show("MBR erased. Please remove and reinsert to format", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }


        /// <summary>
        /// Called to persist registry values on closure so we can remember things like last file used
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainFormFormClosing(object sender, FormClosingEventArgs e)
        {
            var key = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Dynamic Devices Ltd\\DiskImager");
            if (key != null)
            {
                key.SetValue("FileName", textBoxFileName.Text);
                key.Close();
            }

            _watcher.DeviceArrived -= OnDriveArrived;
            _watcher.DeviceRemoved -= OnDriveRemoved;
            StopListenForChanges();
        }

        /// <summary>
        /// Cancels an ongoing read/write
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonCancelClick(object sender, EventArgs e)
        {
            _disk.IsCancelling = true;
        }

        private void RadioButtonCompZipCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        private void RadioButtonCompTgzCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        private void RadioButtonCompGzCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        private void RadioButtonCompNoneCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        #endregion

        #region Implementation

        private void UpdateFileNameText()
        {
            
            var text = textBoxFileName.Text;

         
            text = text.Replace(".tar.gz", "");
            text = text.Replace(".tgz", "");
            text = text.Replace(".tar", "");
            text = text.Replace(".gzip", "");
            text = text.Replace(".gz", "");
            text = text.Replace(".zip", "");

            if (radioButtonCompNone.Checked)
            {
                textBoxFileName.Text = text;
            } else if(radioButtonCompZip.Checked)
            {
                text += ".zip";
                textBoxFileName.Text = text;                
            } else if(radioButtonCompTgz.Checked)
            {
                text += ".tgz";
                textBoxFileName.Text = text;
            }
            else if (radioButtonCompGz.Checked)
            {
                text += ".gz";
                textBoxFileName.Text = text;
            }
        }

        /// <summary>
        /// Select the file for read/write and setup defaults for whether we're using compression based on extension
        /// </summary>
        private void ChooseFile()
        {
            var dr = saveFileDialog1.ShowDialog();

            if (dr != DialogResult.OK)
                return;
            
            textBoxFileName.Text = saveFileDialog1.FileName;
            
            TextBoxFileNameTextChanged(this, null);
        }

        private void TextBoxFileNameTextChanged(object sender, EventArgs e)
        {
            if (textBoxFileName.Text.ToLower().EndsWith(".tar.gz") || textBoxFileName.Text.ToLower().EndsWith(".tgz"))
                radioButtonCompTgz.Checked = true;
            else if (textBoxFileName.Text.ToLower().EndsWith(".gz"))
                radioButtonCompGz.Checked = true;
            else if (textBoxFileName.Text.ToLower().EndsWith(".zip"))
                radioButtonCompZip.Checked = true;
            else if (textBoxFileName.Text.ToLower().EndsWith(".img") || textBoxFileName.Text.ToLower().EndsWith(".bin") || textBoxFileName.Text.ToLower().EndsWith(".sdcard"))
                radioButtonCompNone.Checked = true;

            if (radioButtonCompNone.Checked)
                _eCompType = EnumCompressionType.None;
            else if (radioButtonCompTgz.Checked)
                _eCompType = EnumCompressionType.Targzip;
            else if (radioButtonCompGz.Checked)
                _eCompType = EnumCompressionType.Gzip;
            else if (radioButtonCompZip.Checked)
                _eCompType = EnumCompressionType.Zip;
        }

        private void DisplayAllDrivesToolStripMenuItemCheckedChanged(object sender, EventArgs e)
        {
            PopulateDrives();
            if (comboBoxDrives.Items.Count > 0){
                if (checkBoxLock.Checked == false)
                {
                    if (buttonWrite.Enabled)
                    {
                        EnableButtons();
                    }
                }                
            }
                else
                    DisableButtons(false);
            }

        struct MyDriveType
        {
            public string drivename;
            public bool isNotEmpty;
        }
        /// <summary>
        /// Load in the drives
        /// </summary>
        private void PopulateDrives()
        {
            //bool save_cbstate = comboBoxDrives.Enabled;
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(PopulateDrives));
                return;
            }
            //fill array of removable media drive names
            Dictionary<string,bool> list_drivenames = new Dictionary<string, bool>();
         
            foreach (var drive in DriveInfo.GetDrives())
            {
                // Only display removable drives
                if (drive.IsReady)
                {
                    if (drive.DriveType == DriveType.Removable)
                    {
                        MyDriveType myDrive = new MyDriveType();
                        myDrive.drivename = drive.Name.TrimEnd(new[] { '\\' });
                        myDrive.isNotEmpty = false;  
                        long driveFreeSpace = drive.AvailableFreeSpace;
                        long driveTotalSize = drive.TotalSize;
                        long spaceDiff = (driveTotalSize - driveFreeSpace);
                        if (spaceDiff > 0 ) {
                            //if free space different from total then check if only System Folder used
                            bool isNotSystemFolder = false;
                            try
                            {
                                string[] allfiles = Directory.GetFiles(myDrive.drivename, "*.*", SearchOption.AllDirectories);
                                foreach (string file in allfiles)
                                {
                                    if (file.Contains("System Volume Information") == false)
                                    {
                                        isNotSystemFolder = true;
                                        break;
                                    }
                                }
                            }
                            catch (Exception)
                            {

                                //throw;
                            }


                            //if (sdcardStatus == SdStates.WAIT_INSERT)
                            if ((isNotSystemFolder == true) && (sdcardStatus != SdStates.WAIT_REMOVE))
                            {
                                //sdcardStatus = SdStates.NOT_EMPTY;
                                myDrive.isNotEmpty = true;
                            }                 
                        }
                        list_drivenames.Add(myDrive.drivename,myDrive.isNotEmpty);
                    }
                }

            }
            bool cbSelectedIsNotEmpty = true;
            //list of removable drives is empty
            if (list_drivenames.Count == 0)
            {
                comboBoxDrives.SelectedIndex = -1;
                comboBoxDrives.Items.Clear();
                if (sdcardStatus == SdStates.WAIT_REMOVE)
                {
                    sdcardStatus = SdStates.REMOVED;
                }
                else if (sdcardStatus == SdStates.INIT)
                {
                    labelSdStatus.Text = "INSERT NEW SDCARD";
                }
                if (dispatcherTimer.IsEnabled == false)
                {
                    dispatcherTimer.Start();
                }
                if (checkBoxLock.Checked == false)
                {
                    //checkBoxLock.Enabled = false;
                }
            }
            //list of removable drives is not empty and checkbox unchecked
            else if (checkBoxLock.Checked == false)
            {
                
                
                //Update combobox if different count and not check lock
                if (list_drivenames.Count != comboBoxDrives.Items.Count)
                {
                    comboBoxDrives.SelectedIndex = -1;
                    comboBoxDrives.Items.Clear();
                    foreach (var item in list_drivenames)
                    {
                        comboBoxDrives.Items.Add(item.Key);
                    }
                    comboBoxDrives.SelectedIndex = 0;
                }
                list_drivenames.TryGetValue(comboBoxDrives.SelectedItem.ToString(), out cbSelectedIsNotEmpty);
                if ((sdcardStatus == SdStates.WAIT_INSERT) || (sdcardStatus == SdStates.INIT))
                {
                    if (cbSelectedIsNotEmpty == false)
                    {
                        sdcardStatus = SdStates.INSERTED;
                    }
                    else
                    {
                        sdcardStatus = SdStates.NOT_EMPTY;
                    }
                }
                else if (sdcardStatus == SdStates.INSERTED)
                {
                    if (cbSelectedIsNotEmpty == true)
                    {
                        sdcardStatus = SdStates.NOT_EMPTY;
                    }
                }
            }
            //list of removable drives is not empty and checkbox checked
            else if (checkBoxLock.Checked == true)
            {
                list_drivenames.TryGetValue(checkBoxLock.Text, out cbSelectedIsNotEmpty);

                //check combobox if checked lock and drive name not exists -> clear combobox
                if (list_drivenames.ContainsKey(checkBoxLock.Text) == false)
                {
                    comboBoxDrives.SelectedIndex = -1;
                    comboBoxDrives.Items.Clear();
                    if (sdcardStatus == SdStates.WAIT_REMOVE)
                    {
                        sdcardStatus = SdStates.REMOVED;
                    }
                    
                }
                else if (comboBoxDrives.Items.IndexOf(checkBoxLock.Text) != 0)
                {
                    comboBoxDrives.Items.Clear();
                    comboBoxDrives.Items.Add(checkBoxLock.Text);
                    comboBoxDrives.SelectedIndex = 0;
                    if ((sdcardStatus == SdStates.WAIT_INSERT)|| (sdcardStatus == SdStates.INIT))
                    {
                        if (cbSelectedIsNotEmpty == false)
                        {
                            sdcardStatus = SdStates.INSERTED;
                        }
                        else
                        {
                            sdcardStatus = SdStates.NOT_EMPTY;
                        }
                    }
                }
                else
                {
                    if ((sdcardStatus == SdStates.WAIT_INSERT) || (sdcardStatus == SdStates.INIT))
                    {
                        if (cbSelectedIsNotEmpty == false)
                        {
                            sdcardStatus = SdStates.INSERTED;
                        }
                        else
                        {
                            sdcardStatus = SdStates.NOT_EMPTY;
                        }
                    }
                }

            }

            /*
            foreach (var drive in DriveInfo.GetDrives())
            {
                // Only display removable drives
                if (drive.IsReady)
                {
                    if (drive.DriveType == DriveType.Removable)// || displayAllDrivesToolStripMenuItem.Checked)
                    {
                        if ((checkBoxLock.Text == drive.Name.TrimEnd(new[] { '\\' }))||(checkBoxLock.Checked==false))
                        {
                            comboBoxDrives.Items.Add(drive.Name.TrimEnd(new[] { '\\' }));
                        }
                        
                    }
                }
                
            }
            */
#if false
            //import the System.Management namespace at the top in your "using" statement.
            var searcher = new ManagementObjectSearcher(
                 "SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB'");
            foreach (var disk in searcher.Get())
            {
                var props = disk.Properties;
                foreach(var p in props)
                    Console.WriteLine(p.Name + " = " + p.Value);
            }
#endif
            /*
            if (comboBoxDrives.Items.Count > 0)
            {
                comboBoxDrives.SelectedIndex = 0;
                checkBoxLock.Enabled = true;
            }
            else
            {
                checkBoxLock.Enabled = false;
            }
            */
        }

        /// <summary>
        /// Callback when removable media is inserted or removed, repopulates the drive list
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void WatcherEventArrived(object sender, EventArgs e)
        {
            if(InvokeRequired)
            {
                Invoke(new EventHandler(WatcherEventArrived));
                return;
            }

            PopulateDrives();

            if (comboBoxDrives.Items.Count > 0)
                EnableButtons();
            else
                DisableButtons(false);
        }

        /// <summary>
        /// Updates UI to disable buttons
        /// </summary>
        /// <param name="running">Whether read/write process is running</param>
        private void DisableButtons(bool running)
        {
            buttonRead.Enabled = false;
            buttonWrite.Enabled = false;
            buttonEraseMBR.Enabled = false;
            buttonExit.Enabled = !running;
            buttonCancel.Enabled = running;
            comboBoxDrives.Enabled = false;
            textBoxFileName.Enabled = false;
            buttonChooseFile.Enabled = false;
            groupBoxCompression.Enabled = false;
            groupBoxTruncation.Enabled = false;
            checkBoxLock.Enabled = !running;
        }

        /// <summary>
        /// Updates UI to enable buttons
        /// </summary>
        private void EnableButtons()
        {
            if (checkBoxLock.Checked == false)
            {
                buttonRead.Enabled = true;
                //buttonWrite.Enabled = true;
                buttonEraseMBR.Enabled = true;
                
             
                comboBoxDrives.Enabled = true;
            
                textBoxFileName.Enabled = true;
                buttonChooseFile.Enabled = true;
                groupBoxCompression.Enabled = true;
                groupBoxTruncation.Enabled = true;
                checkBoxLock.Enabled = true;
            }
            buttonExit.Enabled = true;
            buttonCancel.Enabled = false;
            buttonWrite.Enabled = true;
        }

        #endregion

        #region Disk Change Handling

        public bool StartListenForChanges()
        {
            _watcher.DeviceArrived += OnDriveArrived;
            _watcher.DeviceRemoved += OnDriveRemoved;
            return true;
        }

        public void StopListenForChanges()
        {
            if (_watcher != null)
            {
                _watcher.Dispose();
                _watcher = null;
            }
        }

        void OnDriveArrived(object sender, DriveDetectorEventArgs e)
        {
            WatcherEventArrived(sender, e);
        }

        void OnDriveRemoved(object sender, DriveDetectorEventArgs e)
        {
            WatcherEventArrived(sender, e);
        }

        #endregion

        private void checkBoxLock_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox lockBox = (CheckBox)sender;
            if (lockBox.Checked)
            {
                if ((comboBoxDrives.Items.Count>0) && (sdcardStatus == SdStates.INSERTED))
                {
                    string drivenamesave= comboBoxDrives.SelectedItem.ToString();
                    comboBoxDrives.Items.Clear();
                    comboBoxDrives.Items.Add(drivenamesave);
                    comboBoxDrives.SelectedIndex = 0;
                    checkBoxLock.Text = comboBoxDrives.SelectedItem.ToString();
                    comboBoxDrives.Enabled = false;
                    sdcardStatus = SdStates.WAIT_WRITE;
                    labelSdStatus.ForeColor = Color.Black;
                    labelSdStatus.Text = "Press AutoWrite button";
                    DisableButtons(false);
                    buttonWrite.Enabled = true;
                    buttonWrite.Text = "AutoWrite";
                }
                else
                {
                    if (sdcardStatus == SdStates.NOT_EMPTY)
                    {

                    }
                    lockBox.Checked = false;
                    
                }
                //dispatcherTimer.Start();
            }
            else
            {
                checkBoxLock.Text = textCheckBoxLock;
                sdcardStatus = SdStates.INIT;
                labelSdStatus.ForeColor = Color.Black;  
                labelSdStatus.Text = "Manual MODE";
                //dispatcherTimer.Stop();
                EnableButtons();
                buttonWrite.Text = "Write";
            }
        }

        private void comboBoxDrives_DropDown(object sender, EventArgs e)
        {
            PopulateDrives();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {

        }

        private void progressBar1_Click(object sender, EventArgs e)
        {

        }

        private void labelFileName_Click(object sender, EventArgs e)
        {

        }

        private void menuStripMain_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void comboBoxDrives_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (checkBoxLock.Checked == false)
            {
                sdcardStatus = SdStates.INIT;
            }
            
        }
    }
}
