// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Mono.Unix.Native;

namespace Duplicati.Library.Common.IO
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    public static class PosixFile
    {

        private static readonly bool SUPPORTS_LLISTXATTR;

        static PosixFile()
        {
            bool works = false;
            try
            {
                string[] v;
                Mono.Unix.Native.Syscall.llistxattr("/", out v);
                works = true;
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch
            {
            }
            SUPPORTS_LLISTXATTR = works;
        }

        /// <summary>
        /// Opens the file and honors advisory locking.
        /// </summary>
        /// <returns>A open stream that references the file</returns>
        /// <param name="path">The full path to the file</param>
        public static System.IO.Stream OpenExclusive(string path, System.IO.FileAccess mode)
        {
            return OpenExclusive(path, mode, (int)Mono.Unix.Native.FilePermissions.DEFFILEMODE);
        }

        /// <summary>
        /// Opens the file and honors advisory locking.
        /// </summary>
        /// <returns>A open stream that references the file</returns>
        /// <param name="path">The full path to the file</param>
        /// <param name="filemode">The file create mode</param>
        public static System.IO.Stream OpenExclusive(string path, System.IO.FileAccess mode, int filemode)
        {
            Flock lck;
            lck.l_len = 0;
            lck.l_pid = Syscall.getpid();
            lck.l_start = 0;
            lck.l_type = LockType.F_WRLCK;
            lck.l_whence = SeekFlags.SEEK_SET;

            OpenFlags flags = OpenFlags.O_CREAT;
            if (mode == System.IO.FileAccess.Read)
            {
                lck.l_type = LockType.F_RDLCK;
                flags |= OpenFlags.O_RDONLY;
            }
            else if (mode == System.IO.FileAccess.Write)
            {
                flags |= OpenFlags.O_WRONLY;
            }
            else
            {
                flags |= OpenFlags.O_RDWR;
            }

            int fd = Syscall.open(path, flags, (Mono.Unix.Native.FilePermissions)filemode);
            if (fd > 0)
            {
                //This does not work on OSX, it gives ENOTTY
                //int res = Syscall.fcntl(fd, Mono.Unix.Native.FcntlCommand.F_SETLK, ref lck);

                //This is the same (at least for our purpose, and works on OSX)
                int res = Syscall.lockf(fd, LockfCommand.F_TLOCK, 0);

                //If we have the lock, return the stream
                if (res == 0)
                    return new Mono.Unix.UnixStream(fd);
                else
                {
                    Mono.Unix.Native.Syscall.close(fd);
                    throw new LockedFileException(path, mode);
                }
            }

            throw new BadFileException(path);
        }

        [Serializable]
        public abstract class PosixException : System.IO.IOException
        {
            public readonly int ErrorCode;
            public readonly Errno Errno;
            private readonly string _message;
            public override string Message => _message;
            protected PosixException(string message)
            {
                Errno = Syscall.GetLastError();
                ErrorCode = (int)Errno;
                _message = string.Format("{0}, error: {1} ({2})", message, Errno, ErrorCode);
            }
        }

        [Serializable]
        private class BadFileException : PosixException
        {
            public BadFileException(string filename)
                : base(string.Format("Unable to open the file \"{0}\"", filename))
            {
            }
        }

        [Serializable]
        private class LockedFileException : PosixException
        {
            public LockedFileException(string filename, System.IO.FileAccess mode)
                : base(string.Format("Unable to open the file \"{0}\" in mode {1}", filename, mode))
            {
            }
        }

        [Serializable]
        private class FileAccesException : PosixException
        {
            public FileAccesException(string filename, string method)
                : base(string.Format("Unable to access the file \"{0}\" with method {1}", filename, method))
            {
            }
        }

        /// <summary>
        /// Gets the symlink target for the given path
        /// </summary>
        /// <param name="path">The path to get the symlink target for</param>
        /// <returns>The symlink target</returns>
        public static string GetSymlinkTarget(string path)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(2048); //2kb, should cover utf16 * 1023 chars
            if (Mono.Unix.Native.Syscall.readlink(path, sb, (ulong)sb.Capacity) >= 0)
                return sb.ToString();

            throw new System.IO.FileLoadException(string.Format("Unable to get symlink for \"{0}\", error: {1} ({2})", path, Syscall.GetLastError(), (int)Syscall.GetLastError()));
        }

        /// <summary>
        /// Creates a new symlink
        /// </summary>
        /// <param name="path">The path to create the symbolic link entry</param>
        /// <param name="target">The path the symbolic link points to</param>
        public static void CreateSymlink(string path, string target)
        {
            if (Mono.Unix.Native.Syscall.symlink(target, path) != 0)
                throw new System.IO.IOException(string.Format("Unable to create symlink from \"{0}\" to \"{1}\", error: {2} ({3})", path, target, Syscall.GetLastError(), (int)Syscall.GetLastError()));
        }

        /// <summary>
        /// Enum that describes the different filesystem entry types
        /// </summary>
        public enum FileType
        {
            File,
            Directory,
            Symlink,
            Fifo,
            Socket,
            CharacterDevice,
            BlockDevice,
            Unknown
        }

        /// <summary>
        /// Gets the type of the file.
        /// </summary>
        /// <returns>The file type</returns>
        /// <param name="path">The full path to look up</param>
        public static FileType GetFileType(string path)
        {

            var fse = Mono.Unix.UnixFileInfo.GetFileSystemEntry(path);
            if (fse.IsRegularFile)
                return FileType.File;
            else if (fse.IsDirectory)
                return FileType.Directory;
            else if (fse.IsSymbolicLink)
                return FileType.Symlink;
            else if (fse.IsFifo)
                return FileType.Fifo;
            else if (fse.IsSocket)
                return FileType.Socket;
            else if (fse.IsCharacterDevice)
                return FileType.CharacterDevice;
            else if (fse.IsBlockDevice)
                return FileType.BlockDevice;
            else
                return FileType.Unknown;
        }


        /// <summary>
        /// Gets the extended attributes.
        /// </summary>
        /// <returns>The extended attributes.</returns>
        /// <param name="path">The full path to look up</param>
        /// <param name="isSymlink">A flag indicating if the target is a symlink</param>
        /// <param name="followSymlink">A flag indicating if a symlink should be followed</param>
        public static Dictionary<string, byte[]> GetExtendedAttributes(string path, bool isSymlink, bool followSymlink)
        {
            // If we get a symlink that we should not follow, we need llistxattr support
            if (isSymlink && !followSymlink && !SUPPORTS_LLISTXATTR)
                return null;

            var use_llistxattr = SUPPORTS_LLISTXATTR && !followSymlink;

            string[] values;
            var size = use_llistxattr ? Mono.Unix.Native.Syscall.llistxattr(path, out values) : Mono.Unix.Native.Syscall.listxattr(path, out values);
            if (size < 0)
            {
                // In case the underlying filesystem does not support extended attributes,
                // we simply return that there are no attributes
                var err = Syscall.GetLastError();
                if (err == Errno.EOPNOTSUPP || err == Errno.ENODATA)
                    return null;

                throw new FileAccesException(path, use_llistxattr ? "llistxattr" : "listxattr");
            }

            var dict = new Dictionary<string, byte[]>();
            foreach (var s in values)
            {
                byte[] v;
                var n = SUPPORTS_LLISTXATTR ? Mono.Unix.Native.Syscall.lgetxattr(path, s, out v) : Mono.Unix.Native.Syscall.getxattr(path, s, out v);
                if (n > 0)
                    dict.Add(s, v);
            }

            return dict;
        }

        /// <summary>
        /// Sets an extended attribute.
        /// </summary>
        /// <param name="path">The full path to set the values for</param>
        /// <param name="key">The extended attribute key</param>
        /// <param name="value">The value to set</param>
        public static void SetExtendedAttribute(string path, string key, byte[] value)
        {
            Mono.Unix.Native.Syscall.setxattr(path, key, value);
        }

        /// <summary>
        /// Describes the basic user/group/perm tuplet for a file or folder
        /// </summary>
        public struct FileInfo
        {
            public readonly long UID;
            public readonly long GID;
            public readonly long Permissions;
            public readonly string OwnerName;
            public readonly string GroupName;

            internal FileInfo(Mono.Unix.UnixFileSystemInfo fse)
            {
                UID = fse.OwnerUserId;
                GID = fse.OwnerGroupId;
                Permissions = (long)fse.FileAccessPermissions;

                try
                {
                    OwnerName = fse.OwnerUser.UserName;
                }
                catch (ArgumentException)
                {
                    // Could not retrieve user name, possibly the user is not defined on the local system
                    OwnerName = null;
                }

                try
                {
                    GroupName = fse.OwnerGroup.GroupName;
                }
                catch (ArgumentException)
                {
                    // Could not retrieve group name, possibly the group is not defined on the local system
                    GroupName = null;
                }
            }
        }

        /// <summary>
        /// Gets the basic user/group/perm tuplet for a file or folder
        /// </summary>
        /// <returns>The basic user/group/perm tuplet for a file or folder</returns>
        /// <param name="path">The full path to look up</param>
        public static FileInfo GetUserGroupAndPermissions(string path)
        {
            return new FileInfo(Mono.Unix.UnixFileInfo.GetFileSystemEntry(path));
        }

        /// <summary>
        /// Sets the basic user/group/perm tuplet for a file or folder
        /// </summary>
        /// <param name="path">The full path to look up</param>
        /// <param name="uid">The owner user id to set</param>
        /// <param name="gid">The owner group id to set</param>
        /// <param name="permissions">The file access permissions to set</param>
        public static void SetUserGroupAndPermissions(string path, long uid, long gid, long permissions)
        {
            Mono.Unix.UnixFileInfo.GetFileSystemEntry(path).SetOwner(uid, gid);
            Mono.Unix.UnixFileInfo.GetFileSystemEntry(path).FileAccessPermissions = (Mono.Unix.FileAccessPermissions)permissions;
        }

        /// <summary>
        /// Gets the UID from a user name
        /// </summary>
        /// <returns>The user ID.</returns>
        /// <param name="name">The user name.</param>
        public static long GetUserID(string name)
        {
            return new Mono.Unix.UnixUserInfo(name).UserId;
        }

        /// <summary>
        /// Gets the GID from a group name
        /// </summary>
        /// <returns>The user ID.</returns>
        /// <param name="name">The group name.</param>
        public static long GetGroupID(string name)
        {
            return new Mono.Unix.UnixGroupInfo(name).GroupId;
        }

        /// <summary>
        /// Gets the number of hard links for a file
        /// </summary>
        /// <returns>The hardlink count</returns>
        /// <param name="path">The full path to look up</param>
        public static long GetHardlinkCount(string path)
        {
            var fse = Mono.Unix.UnixFileInfo.GetFileSystemEntry(path);
            if (fse.IsRegularFile || fse.IsDirectory)
                return fse.LinkCount;
            else
                return 0;
        }

        /// <summary>
        /// Gets a unique ID for the path inode target,
        /// which is the device ID and inode ID
        /// joined with a &quot;:&quot;
        /// </summary>
        /// <returns>The inode target ID.</returns>
        /// <param name="path">The full path to look up</param>
        public static string GetInodeTargetID(string path)
        {
            var fse = Mono.Unix.UnixFileInfo.GetFileSystemEntry(path);
            return fse.Device + ":" + fse.Inode;
        }
    }
}

