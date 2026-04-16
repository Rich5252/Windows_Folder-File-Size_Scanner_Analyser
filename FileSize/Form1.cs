using System.Collections.Concurrent;
using System.IO;
using System.Timers;
using static FileSize.Form1;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;



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
        }

        private void FlushUpdateBucket(object sender, EventArgs e)
        {
            if (_updateBucket.IsEmpty) return;

            // Use a HashSet to keep track of which parents need a re-sort 
            // so we only sort each parent ONCE per tick.
            HashSet<TreeNode> parentsToSort = new HashSet<TreeNode>();

            treeView1.BeginUpdate();
            try
            {
                // Limit the number of items we process per tick (e.g., 500)
                // This prevents a massive queue from locking the UI thread forever.
                int processedCount = 0;
                while (_updateBucket.TryDequeue(out var update) && processedCount < 500)
                {
                    processedCount++;
                    var parent = update.ParentNode;
                    var current = update.CurrentNode;

                    if (update.IsFolder)
                    {
                        current.Tag = update.TotalSize;
                        // Update the text to show the new size
                        string nameOnly = Path.GetFileName(current.Name);
                        current.Text = $"{nameOnly} - [{FormatSize(update.TotalSize)}]";
                    }

                    // If the node isn't in the tree yet, add it
                    if (current.TreeView == null)
                    {
                        parent.Nodes.Add(current);
                    }

                    parentsToSort.Add(parent);
                }

                // Now sort the parents that were modified
                foreach (var parent in parentsToSort)
                {
                    SortFolderNodes(parent);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Flush Error: " + ex.Message);
            }
            finally
            {
                // THIS MUST RUN or the TreeView will stop painting entirely
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

        private long ScanDirectorySafe(DirectoryInfo dir, TreeNode node)
        {
            long totalSize = 0;

            try
            {
                // 1. Process Files
                foreach (var file in dir.GetFiles())
                {
                    totalSize += file.Length;
                    this.Invoke(() => {
                        node.Nodes.Add(new TreeNode($"{file.Name} [{FormatSize(file.Length)}]"));
                    });
                }

                // 2. Process Subdirectories
                foreach (var subDir in dir.GetDirectories())
                {
                    // SKIP JUNCTION POINTS (This is why User folders fail!)
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        continue;

                    var subNode = new TreeNode(subDir.Name);
                    this.Invoke(() => node.Nodes.Add(subNode));

                    // Recursive call to get the size of the child
                    long subDirSize = ScanDirectorySafe(subDir, subNode);
                    totalSize += subDirSize;

                    // Update the node text with its total calculated size
                    this.Invoke(() => subNode.Text += $" - [{FormatSize(subDirSize)}]");
                }
            }
            catch (UnauthorizedAccessException)
            {
                this.Invoke(() => {
                    node.ForeColor = Color.Red;
                    node.Text += " (Locked)";
                });
            }

            return totalSize;
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
            public TreeNode ParentNode { get; set; }
            public TreeNode CurrentNode { get; set; }
            public long TotalSize { get; set; }
            public bool IsFolder { get; set; }
        }

        private async void btnScan_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                treeView1.Nodes.Clear();

                // 1. Setup the Root
                var rootDir = new DirectoryInfo(fbd.SelectedPath);
                var rootNode = new TreeNode(rootDir.Name) { Name = rootDir.FullName, Tag = 0L };
                treeView1.Nodes.Add(rootNode);

                // 2. Start the Timer (The "Bucket Flusher")
                _updateBucket.Clear();                      // Clear any old leftovers
                _uiUpdateTimer.Start();

                // 3. Run the scan (The "Bucket Filler")
                // Note: No 'progress' object passed here anymore!
                await Task.Run(() => SafeDynamicScan(rootDir, rootNode));

                // 4. Optional: Stop timer after scan is complete and do one final flush
                // _uiUpdateTimer.Stop();
                // FlushUpdateBucket(null, null);
            }
        }

        private long SafeDynamicScan(DirectoryInfo dir, TreeNode node)
        {
            long currentDirSize = 0;

            try
            {
                // 1. Process Files
                foreach (var file in dir.GetFiles())
                {
                    currentDirSize += file.Length;

                    var fileNode = new TreeNode($"{file.Name} ({FormatSize(file.Length)})")
                    {
                        Name = file.FullName, // Store Path
                        Tag = file.Length     // Store Size for sorting
                    };

                    // DROP INTO BUCKET
                    _updateBucket.Enqueue(new ScanUpdate { ParentNode = node, CurrentNode = fileNode, IsFolder = false });
                }

                // 2. Process Subdirectories
                foreach (var subDir in dir.GetDirectories())
                {
                    // Skip Junctions/Shortcuts
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;

                    var subNode = new TreeNode(subDir.Name)
                    {
                        Name = subDir.FullName,
                        Tag = 0L
                    };

                    // DROP INTO BUCKET (Initial folder discovery)
                    _updateBucket.Enqueue(new ScanUpdate { ParentNode = node, CurrentNode = subNode, IsFolder = true });

                    // RECURSE
                    long subSize = SafeDynamicScan(subDir, subNode);
                    currentDirSize += subSize;

                    // DROP INTO BUCKET (Update with final calculated size)
                    _updateBucket.Enqueue(new ScanUpdate { ParentNode = node, CurrentNode = subNode, TotalSize = subSize, IsFolder = true });
                }
            }
            catch (UnauthorizedAccessException) { /* Locked folder skipped */ }

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

        private void UpdateAndSortNode(ScanUpdate update)
        {
            treeView1.BeginUpdate();

            var parent = update.ParentNode;
            var current = update.CurrentNode;

            if (update.IsFolder)
            {
                // Use the 'Name' property to hold the raw path (it's a string)
                if (string.IsNullOrEmpty(current.Name))
                    current.Name = (string)current.Tag; // Fallback if path was in Tag

                // Now update the display text using the path stored in Name
                string originalName = Path.GetFileName(current.Name);
                current.Text = $"{originalName} - [{FormatSize(update.TotalSize)}]";

                // Store the Size in Tag specifically for the sort comparison
                current.Tag = update.TotalSize;
            }

            if (!parent.Nodes.Contains(current))
            {
                parent.Nodes.Add(current);
            }

            // --- SORTING ---
            if (parent.Nodes.Count > 1)
            {
                var nodes = parent.Nodes.Cast<TreeNode>()
                    .OrderByDescending(n => n.Tag is long l ? l : 0L)
                    .ToArray();

                parent.Nodes.Clear();
                parent.Nodes.AddRange(nodes);
            }

            treeView1.EndUpdate();
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
    }
}
