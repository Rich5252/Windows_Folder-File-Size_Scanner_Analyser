using System.Collections.Concurrent;
using System.Reflection;



namespace FileSize
{
    public partial class Form1 : Form
    {
        // A thread-safe bucket to hold updates until the UI is ready
        private ConcurrentQueue<ScanUpdate> _updateBucket = new();
        private System.Windows.Forms.Timer _uiUpdateTimer;

        public Form1()
        {
            InitializeComponent();

            // Initialize a timer to flush the bucket every 150ms
            _uiUpdateTimer = new System.Windows.Forms.Timer();
            _uiUpdateTimer.Interval = 150;
            _uiUpdateTimer.Tick += FlushUpdateBucket;

            EnableDoubleBuffering();        //for treeview control to prevent flickering
        }

        private Dictionary<string, TreeNode> _pathMap = new();

        private void FlushUpdateBucket(object sender, EventArgs e)
        {
            if (_updateBucket.IsEmpty) return;

            treeView1.BeginUpdate();
            try
            {
                int batchLimit = 1000;
                while (_updateBucket.TryDequeue(out var update) && batchLimit-- > 0)
                {
                    // 1. Find or Create the Parent Node
                    if (!_pathMap.TryGetValue(update.ParentPath, out TreeNode parentNode)) continue;

                    // 2. Find or Create the Current Node
                    if (!_pathMap.TryGetValue(update.FullPath, out TreeNode currentNode))
                    {
                        currentNode = new TreeNode(update.ItemName) { Name = update.FullPath };
                        parentNode.Nodes.Add(currentNode);
                        _pathMap[update.FullPath] = currentNode;
                    }

                    // 3. Update Data
                    currentNode.Tag = update.Size;
                    if (update.IsFolder)
                    {
                        currentNode.Text = $"{update.ItemName} - [{FormatSize(update.Size)}]";
                    }
                    else
                    {
                        currentNode.Text = $"{update.ItemName} ({FormatSize(update.Size)})";
                    }

                    // 4. Mark parent for sorting (Optional: only sort every 5th tick to save CPU)
                    SortFolderNodes(parentNode);
                }
            }
            finally
            {
                treeView1.EndUpdate();
            }
        }


        private void SortFolderNodes(TreeNode parent)
        {
            if (parent.Nodes.Count < 2) return;

            // Capture nodes, sort them by the 'long' size in the Tag, and re-add
            var sortedNodes = parent.Nodes.Cast<TreeNode>()
                .OrderByDescending(n => n.Tag is long l ? l : 0L)
                .ToArray();

            // Check if the order actually changed before clearing (performance boost)
            parent.Nodes.Clear();
            parent.Nodes.AddRange(sortedNodes);
        }

       
        public long GetDirectorySize(DirectoryInfo d)
        {
            long size = 0;
            // Add file sizes
            FileInfo[] fis = d.GetFiles();
            foreach (FileInfo fi in fis)
            {
                size += fi.Length;
            }
            // Add subdirectory sizes
            DirectoryInfo[] dis = d.GetDirectories();
            foreach (DirectoryInfo di in dis)
            {
                size += GetDirectorySize(di);
            }
            return size;
        }

        // Simple class to pass data back to the UI
        public class ScanUpdate
        {
            public string ParentPath { get; set; } = ""; // Use path as the key
            public string ItemName { get; set; } = "";
            public string FullPath { get; set; } = "";
            public long Size { get; set; }
            public bool IsFolder { get; set; }
        }

        private async void btnScan_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                treeView1.Nodes.Clear();
                _pathMap.Clear();
                var rootDir = new DirectoryInfo(fbd.SelectedPath);
                var rootNode = new TreeNode(rootDir.Name) { Name = rootDir.FullName };
                treeView1.Nodes.Add(rootNode);
                _pathMap[rootDir.FullName] = rootNode; // Add the root to the map!

                _uiUpdateTimer.Start();
                await Task.Run(() => SafeDynamicScan(rootDir));
            }
        }

        private long SafeDynamicScan(DirectoryInfo dir)
        {
            long currentDirSize = 0;

            try
            {
                foreach (var file in dir.GetFiles())
                {
                    currentDirSize += file.Length;
                    _updateBucket.Enqueue(new ScanUpdate
                    {
                        ParentPath = dir.FullName,
                        ItemName = file.Name,
                        FullPath = file.FullName,
                        Size = file.Length,
                        IsFolder = false
                    });
                    Thread.Sleep(10); // Simulate delay for testing UI responsiveness
                }

                foreach (var subDir in dir.GetDirectories())
                {
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;

                    // Initial folder discovery
                    _updateBucket.Enqueue(new ScanUpdate
                    {
                        ParentPath = dir.FullName,
                        ItemName = subDir.Name,
                        FullPath = subDir.FullName,
                        IsFolder = true
                    });


                    long subSize = SafeDynamicScan(subDir);
                    currentDirSize += subSize;

                    // Size update for folder
                    _updateBucket.Enqueue(new ScanUpdate
                    {
                        ParentPath = dir.FullName,
                        ItemName = subDir.Name,
                        FullPath = subDir.FullName,
                        Size = subSize,
                        IsFolder = true
                    });
                    Thread.Sleep(10);               // Simulate delay for testing UI responsiveness
                }
            }
            catch (UnauthorizedAccessException) { Thread.Sleep(1); }
            return currentDirSize;
        }

        private FolderModel BuildModel(DirectoryInfo dir)
        {
            var model = new FolderModel { Name = dir.Name, FullPath = dir.FullName };

            try
            {
                // Add Files
                foreach (var file in dir.GetFiles())
                {
                    model.Files.Add(new FileModel(file.Name, file.Length));
                    model.TotalSize += file.Length;
                }

                // Add Subfolders
                foreach (var subDir in dir.GetDirectories())
                {
                    // Skip Junctions/Reparse Points to avoid User folder errors
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;

                    var subModel = BuildModel(subDir);
                    model.SubFolders.Add(subModel);
                    model.TotalSize += subModel.TotalSize; // Bubble the size up
                }
            }
            catch (UnauthorizedAccessException) { /* Log or skip */ }

            return model;
        }

        private void SortModel(FolderModel model)
        {
            // Sort subfolders by size descending
            model.SubFolders = model.SubFolders.OrderByDescending(f => f.TotalSize).ToList();

            // Sort files by size descending
            model.Files = model.Files.OrderByDescending(f => f.Size).ToList();

            // Recursively sort subfolders
            foreach (var sub in model.SubFolders)
            {
                SortModel(sub);
            }
        }

        private TreeNode ConvertModelToTreeNode(FolderModel model)
        {
            var node = new TreeNode($"{model.Name} [{FormatSize(model.TotalSize)}]");

            foreach (var sub in model.SubFolders)
            {
                node.Nodes.Add(ConvertModelToTreeNode(sub));
            }

            foreach (var file in model.Files)
            {
                node.Nodes.Add(new TreeNode($"{file.Name} ({FormatSize(file.Size)})"));
            }

            return node;
        }

        private TreeNode CreateDirectoryNode(DirectoryInfo directoryInfo)
        {
            long size = 0;
            var directoryNode = new TreeNode(directoryInfo.Name);

            try
            {
                // Files in this folder
                foreach (var file in directoryInfo.GetFiles())
                {
                    size += file.Length;
                    directoryNode.Nodes.Add(new TreeNode($"{file.Name} ({FormatSize(file.Length)})"));
                }

                // Subdirectories
                foreach (var directory in directoryInfo.GetDirectories())
                {
                    var subNode = CreateDirectoryNode(directory);
                    // Add the size of the subnode back to our current total
                    // (Note: In a production app, you'd store the 'long' size in the .Tag property)
                    directoryNode.Nodes.Add(subNode);
                }
            }
            catch (UnauthorizedAccessException)
            {
                directoryNode.Nodes.Add("Access Denied");
            }

            directoryNode.Text += $" - [{FormatSize(GetDirectorySize(directoryInfo))}]";
            return directoryNode;
        }

        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            treeView1.Width = this.ClientSize.Width - 20;
            treeView1.Height = this.ClientSize.Height - 60;
        }

        private void EnableDoubleBuffering()
        {
            // This tells Windows to paint the TreeView in memory before showing it on screen,
            // which eliminates the "white flash" and flickering during fast updates.
            typeof(System.Windows.Forms.TreeView).InvokeMember("DoubleBuffered",
                BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.NonPublic,
                null, treeView1, new object[] { true });
        }
    }
}
