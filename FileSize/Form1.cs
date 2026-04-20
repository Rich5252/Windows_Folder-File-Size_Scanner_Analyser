using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;



namespace FileSize
{
    public partial class Form1 : Form
    {
        // A thread-safe bucket to hold updates until the UI is ready
        private ConcurrentQueue<ScanUpdate> _updateBucket = new();
        private System.Windows.Forms.Timer _uiUpdateTimer;
        private CancellationTokenSource _cts;

        public Form1()
        {
            InitializeComponent();

            // Initialize a timer to flush the bucket every 150ms
            _uiUpdateTimer = new System.Windows.Forms.Timer();
            _uiUpdateTimer.Interval = 150;
            _uiUpdateTimer.Tick += FlushUpdateBucket;

            EnableDoubleBuffering();        //for treeview control to prevent flickering
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            treeView1.Width = this.ClientSize.Width / 3;
            treeView1.Height = this.statusStrip1.Top - 10 - treeView1.Top;
            listViewFiles.Left = treeView1.Right + 10;
            listViewFiles.Width = this.ClientSize.Width - treeView1.Width - 30;
            listViewFiles.Height = treeView1.Height;
            listViewFiles.Columns[0].Width = listViewFiles.Width / 3 * 2;
            listViewFiles.Columns[1].Width = listViewFiles.Width / 6;
            listViewFiles.Columns[2].Width = listViewFiles.Width / 6;
        }


        private Dictionary<string, TreeNode> _pathMap = new();

        // Simple class to pass data back to the UI from the background scanner
        public class ScanUpdate
        {
            public string ParentPath { get; set; } = ""; // Use path as the key
            public string ItemName { get; set; } = "";
            public string FullPath { get; set; } = "";
            public long Size { get; set; }
            public bool IsFolder { get; set; }
        }


        private DirectoryInfo rootDir;          //the selected directory to scan
        private async void btnScan_Click(object sender, EventArgs e)
        {
            // 1. If a scan is already running, stop it first!
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }

            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                // 2. Initialize a fresh token for this specific scan
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                // Reset UI and Data
                _updateBucket.Clear();
                _pathMap.Clear();
                treeView1.Nodes.Clear();
                listViewFiles.Items.Clear(); // Clear your file pane too

                rootDir = new DirectoryInfo(fbd.SelectedPath);
                var rootNode = new TreeNode(rootDir.Name)
                {
                    Name = rootDir.FullName, // This is the key for the dictionary
                    Tag = 0L
                };

                rootNode.Nodes.Add("Loading..."); // Add the trigger for the expand event
                treeView1.Nodes.Add(rootNode);
                _pathMap[rootDir.FullName] = rootNode;

                // Reset counters and start timers
                nFolder = 0;
                nFile = 0;
                _uiUpdateTimer.Start(); // The bucket flusher
                timer1.Start();         // Your stats timer

                try
                {
                    // 3. Pass the token into the background task
                    await Task.Run(() => SafeDynamicScan(rootDir, token), token);

                    // Success! 
                    timer1.Stop();
                    statusStrip1.Items[0].Text = $"Folders: {nFolder} | Files: {nFile}";
                    // Give the bucket one final flush to catch the last items
                    FlushUpdateBucket(null, null);
                    //_uiUpdateTimer.Stop();
                }
                catch (OperationCanceledException)
                {
                    // This happens if the user hits "Stop"
                    this.Text = "Scan Cancelled";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error: {ex.Message}");
                }
            }
        }

        private int nFolder = 0;
        private int nFile = 0;

        // Use this to store the "Source of Truth" for folder sizes and children
        private ConcurrentDictionary<string, long> _folderSizes = new();
        private ConcurrentDictionary<string, List<string>> _folderStructure = new();

        //The scan runs in the background, but it reports progress by adding "ScanUpdate" items to the _updateBucket queue,
        //which the UI timer flushes every 150ms.
        //This way we avoid cross-thread issues and keep the UI responsive.
        private long SafeDynamicScan(DirectoryInfo dir, CancellationToken token)
        {
            if (token.IsCancellationRequested) return 0;
            long currentDirSize = 0;
            var subDirs = new List<string>();

            try
            {
                // 1. Files
                foreach (var file in dir.GetFiles())
                {
                    if (token.IsCancellationRequested) return 0;
                    currentDirSize += file.Length;
                    Interlocked.Increment(ref nFile);
                }

                // 2. Subdirectories - Discovery phase
                DirectoryInfo[] discoveredDirs = dir.GetDirectories();
                foreach (var subDir in discoveredDirs)
                {
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                    subDirs.Add(subDir.FullName);
                }

                // Save structure so UI can see children immediately
                _folderStructure[dir.FullName] = subDirs;

                // 3. Recursion phase
                foreach (var subPath in subDirs)
                {
                    currentDirSize += SafeDynamicScan(new DirectoryInfo(subPath), token);
                    Interlocked.Increment(ref nFolder);

                    if (dir.FullName == rootDir.FullName)
                    {
                        this.Invoke(() => { treeView1.Nodes[0].Text = $"{dir.Name} - [{FormatSize(currentDirSize)}]"; });
                        Thread.Sleep(10);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // If we can't read this folder, ensure we at least mark it empty in structure
                _folderStructure[dir.FullName] = new List<string>();
            }
            catch (Exception) { /* Catch-all for other IO issues */ }

            _folderSizes[dir.FullName] = currentDirSize;

            if (dir.Name == "ViPEC")
            {
                if (true) { _folderSizes[dir.FullName] = currentDirSize; }
                
            }

            // Final signal for UI text/sorting update
            _updateBucket.Enqueue(new ScanUpdate
            {
                ParentPath = dir.Parent?.FullName ?? "",
                FullPath = dir.FullName,
                ItemName = dir.Name,
                Size = currentDirSize,
                IsFolder = true
            });

            return currentDirSize;
        }


        //Called from a timer on the UI thread, so we can safely update the TreeView without cross-thread issues.
        // Receives "ScanUpdate" items from the _updateBucket queue and applies them to the TreeView nodes.
        // Only updates nodes that are already in the _pathMap (i.e., visible/expanded),
        //      and it does so in batches to keep the UI responsive.
        private void FlushUpdateBucket(object sender, EventArgs e)
        {
            if (_updateBucket.IsEmpty) return;
            _uiUpdateTimer.Stop();

            HashSet<TreeNode> dirtyParents = new HashSet<TreeNode>();
            var watch = Stopwatch.StartNew();

            treeView1.BeginUpdate();
            try
            {
                while (_updateBucket.TryDequeue(out var update) && watch.ElapsedMilliseconds < 50)
                {
                    // Only update nodes already in the map (visible/expanded)
                    if (_pathMap.TryGetValue(update.FullPath, out TreeNode currentNode))
                    {
                        currentNode.Tag = update.Size;
                        currentNode.Text = $"{update.ItemName} - [{FormatSize(update.Size)}]";

                        // If user is looking at an expanded folder that was empty, fill it now
                        if (currentNode.IsExpanded && (currentNode.Nodes.Count == 0 ||
                            (currentNode.Nodes.Count == 1 && currentNode.Nodes[0].Text == "Loading...")))
                        {
                            PopulateNode(currentNode);
                        }

                        if (currentNode.Parent != null)
                            dirtyParents.Add(currentNode.Parent);
                    }
                }

                foreach (var parent in dirtyParents)
                {
                    SortFolderNodes(parent);
                }
            }
            finally
            {
                treeView1.EndUpdate();
                if (!_updateBucket.IsEmpty) _uiUpdateTimer.Start();
            }
        }


        private void PopulateNode(TreeNode parentNode)
        {
            string path = parentNode.Name;
            if (_folderStructure.TryGetValue(path, out List<string> children))
            {
                parentNode.Nodes.Clear();
                foreach (var childPath in children)
                {
                    var di = new DirectoryInfo(childPath);
                    long size = _folderSizes.GetValueOrDefault(childPath, 0);

                    TreeNode childNode = new TreeNode($"{di.Name} - [{FormatSize(size)}]")
                    {
                        Name = childPath,
                        Tag = size
                    };

                    _pathMap[childPath] = childNode;

                    // FIX: Instead of calling GetDirectories(), check our own cache 
                    // OR use a safe check that won't crash the UI
                    if (_folderStructure.ContainsKey(childPath) && _folderStructure[childPath].Count > 0)
                    {
                        childNode.Nodes.Add("Loading...");
                    }
                    else
                    {
                        // Fallback: only add dummy if we can actually access it
                        try
                        {
                            if (di.Exists && di.GetDirectories().Length > 0)
                                childNode.Nodes.Add("Loading...");
                        }
                        catch { /* Ignore folders we can't peek into */ }
                    }

                    parentNode.Nodes.Add(childNode);
                }
                SortFolderNodes(parentNode);
            }
        }

        private void SortFolderNodes(TreeNode parent)
        {
            if (parent == null || parent.Nodes.Count < 2) return;

            // Filter out the "Loading..." dummy to avoid casting errors
            var children = parent.Nodes.Cast<TreeNode>()
                                       .Where(n => n.Text != "Loading...")
                                       .ToList();

            if (children.Count < 2) return;

            // Create the desired order based on the Tag
            var sorted = children.OrderByDescending(n => n.Tag is long l ? l : 0L).ToList();

            // Only touch the UI if the order is actually wrong
            if (!children.SequenceEqual(sorted))
            {
                parent.Nodes.Clear();
                parent.Nodes.AddRange(sorted.ToArray());
            }
        }



        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (number >= 1024)
            {
                number /= 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

       
        private void EnableDoubleBuffering()
        {
            // This tells Windows to paint the TreeView in memory before showing it on screen,
            // which eliminates the "white flash" and flickering during fast updates.
            typeof(System.Windows.Forms.TreeView).InvokeMember("DoubleBuffered",
                BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.NonPublic,
                null, treeView1, new object[] { true });
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            timer1.Stop();
            statusStrip1.Items[0].Text = $"Folders: {nFolder} | Files: {nFile}";
            timer1.Start();
        }


        private void treeView1_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Text == "Loading...")
            {
                PopulateNode(e.Node);
            }
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            string path = e.Node.Name; // We stored the FullPath here
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            listViewFiles.Items.Clear();
            listViewFiles.BeginUpdate();

            try
            {
                DirectoryInfo di = new DirectoryInfo(path);

                // Get files and sort them descending by size
                var files = di.GetFiles().OrderByDescending(f => f.Length);

                foreach (var file in files)
                {
                    var item = new ListViewItem(file.Name);
                    item.Name = file.FullName; // Store full path for context menu actions
                    item.SubItems.Add(FormatSize(file.Length));
                    item.SubItems.Add(file.Extension.ToUpper().Replace(".", "") + " File");

                    listViewFiles.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                listViewFiles.Items.Add(new ListViewItem($"Access Denied: {ex.Message}"));
            }
            finally
            {
                listViewFiles.EndUpdate();
            }
        }

        private void treeView1_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // Ensure the node clicked is actually selected before the menu pops up
            if (e.Button == MouseButtons.Right)
            {
                treeView1.SelectedNode = e.Node;
            }
        }

        private void openInFileExplorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string path = "";

            // Check if the TreeView or ListView is focused
            if (treeView1.Focused)
                path = treeView1.SelectedNode?.Name;
            else if (listViewFiles.Focused && listViewFiles.SelectedItems.Count > 0)
                path = listViewFiles.SelectedItems[0].Name; // Ensure you set .Name when adding items to ListView!

            if (!string.IsNullOrEmpty(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }

    }
}
