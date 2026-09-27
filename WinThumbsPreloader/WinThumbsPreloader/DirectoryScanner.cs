using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinThumbsPreloader
{
    public class DirectoryScanner
    {
        //Number of directory reads that failed (access denied, too long, vanished mid-walk).
        public int unreadableDirectories = 0;
        public string lastUnreadablePath = "";
        public string lastUnreadableReason = "";

        private string path;
        private bool includeNestedDirectories;
        private bool includeDirectories;
        private bool followReparsePoints;

        public DirectoryScanner(string path, bool includeNestedDirectories)
            : this(path, includeNestedDirectories, true, true)
        {
        }

        public DirectoryScanner(string path, bool includeNestedDirectories, bool includeDirectories)
            : this(path, includeNestedDirectories, includeDirectories, true)
        {
        }

        public DirectoryScanner(string path, bool includeNestedDirectories, bool includeDirectories, bool followReparsePoints)
        {
            this.path = path;
            this.includeNestedDirectories = includeNestedDirectories;
            this.includeDirectories = includeDirectories;
            this.followReparsePoints = followReparsePoints;
        }

        public void ResetCounters()
        {
            unreadableDirectories = 0;
            lastUnreadablePath = "";
            lastUnreadableReason = "";
        }

        //Junctions and symlinks can point at their own ancestors. Walking one without
        //checking would loop forever, so callers that walk a whole drive opt out.
        private bool IsReparsePoint(string directoryPath)
        {
            try
            {
                return (File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private void ReportUnreadable(string directoryPath, Exception error)
        {
            unreadableDirectories++;
            lastUnreadablePath = directoryPath;
            lastUnreadableReason = (error == null ? "unknown error" : error.Message);
        }

        public IEnumerable<string> GetItems()
        {
            if (includeNestedDirectories)
            {
                foreach (string item in GetItemsNested()) yield return item;
            }
            else
            {
                foreach (string item in GetItemsOnlyFirstLevel()) yield return item;
            }
        }

        private IEnumerable<string> GetItemsOnlyFirstLevel()
        {
            string[] items = null;
            try
            {
                items = Directory.GetFileSystemEntries(path).ToArray();
            }
            catch (Exception e)
            {
                ReportUnreadable(path, e);
            }
            if (items != null)
            {
                for (int itemIndex = 0; itemIndex < items.Length; itemIndex++)
                {
                    if (!includeDirectories && Directory.Exists(items[itemIndex])) continue;
                    yield return items[itemIndex];
                }
            }
        }

        private IEnumerable<string> GetItemsNested()
        {
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(path);
            string currentPath;
            while (queue.Count > 0)
            {
                currentPath = queue.Dequeue();
                if (includeDirectories) yield return currentPath;
                string[] files = null;
                try
                {
                    if (followReparsePoints || !IsReparsePoint(currentPath))
                    {
                        foreach (string subDirectory in Directory.GetDirectories(currentPath))
                        {
                            if (followReparsePoints || !IsReparsePoint(subDirectory)) queue.Enqueue(subDirectory);
                        }
                    }
                    files = Directory.GetFiles(currentPath);
                }
                catch (Exception e)
                {
                    ReportUnreadable(currentPath, e);
                }
                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++) yield return files[i];
                }
            }
        }

        public IEnumerable<int> GetItemsCount()
        {
            if (includeNestedDirectories)
            {
                foreach (int itemsCount in GetItemsCountNested()) yield return itemsCount;
            }
            else
            {
                foreach (int itemsCount in GetItemsCountOnlyFirstLevel()) yield return itemsCount;
            }
        }

        private IEnumerable<int> GetItemsCountOnlyFirstLevel()
        {
            string[] items = null;
            try
            {
                items = Directory.GetFileSystemEntries(path);
            }
            catch (Exception e)
            {
                ReportUnreadable(path, e);
            }
            if (items == null) yield break;
            int itemsCount = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (includeDirectories || !Directory.Exists(items[i])) itemsCount++;
            }
            if (itemsCount > 0) yield return itemsCount;
        }

        //Mirrors GetItemsNested exactly so the total matches the number of processed items.
        //Every folder that is dequeued contributes its own entry plus its files, so
        //enqueuing a subfolder must not add anything: it is counted when dequeued.
        private IEnumerable<int> GetItemsCountNested()
        {
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(path);
            string currentPath;
            int itemsCount;
            while (queue.Count > 0)
            {
                currentPath = queue.Dequeue();
                itemsCount = 0;
                try
                {
                    if (followReparsePoints || !IsReparsePoint(currentPath))
                    {
                        foreach (string subDir in Directory.GetDirectories(currentPath))
                        {
                            if (followReparsePoints || !IsReparsePoint(subDir)) queue.Enqueue(subDir);
                        }
                    }
                    itemsCount += Directory.GetFiles(currentPath).Length;
                }
                catch (Exception e)
                {
                    ReportUnreadable(currentPath, e);
                }
                if (includeDirectories) itemsCount++;
                if (itemsCount > 0) yield return itemsCount;
            }
        }
    }
}
