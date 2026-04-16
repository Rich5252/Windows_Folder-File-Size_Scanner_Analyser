using System;
using System.Collections.Generic;
using System.Text;

namespace FileSize
{
    public class FolderModel
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public long TotalSize { get; set; }
        public List<FolderModel> SubFolders { get; set; } = new();
        public List<FileModel> Files { get; set; } = new();
    }

    public record FileModel(string Name, long Size);

}
