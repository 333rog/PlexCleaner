﻿using InsaneGenius.Utilities;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace PlexCleaner
{
    public class SidecarFile
    {
        public static bool IsSidecarFile(string filename)
        {
            return IsSidecarExtension(Path.GetExtension(filename));
        }

        public static bool IsSidecarFile(FileInfo fileinfo)
        {
            if (fileinfo == null)
                throw new ArgumentNullException(nameof(fileinfo));

            return IsSidecarExtension(fileinfo.Extension);
        }

        public static bool IsSidecarExtension(string extension)
        {
            if (extension == null)
                throw new ArgumentNullException(nameof(extension));

            return extension.Equals(SidecarExtension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool CreateSidecarFile(FileInfo mediaFile)
        {
            SidecarFile sidecarfile = new SidecarFile();
            return sidecarfile.CreateSidecar(mediaFile);
        }

        public static bool DoesSidecarExist(FileInfo mediaFile)
        {
            if (mediaFile == null)
                throw new ArgumentNullException(nameof(mediaFile));

            // Does the sidecar file exist for this media file
            string sidecarName = Path.ChangeExtension(mediaFile.FullName, SidecarExtension);
            return File.Exists(sidecarName);
        }

        public static string GetSidecarName(FileInfo mediaFile)
        {
            if (mediaFile == null)
                throw new ArgumentNullException(nameof(mediaFile));

            return Path.ChangeExtension(mediaFile.FullName, SidecarExtension);
        }

        public static bool GetMediaInfo(FileInfo mediaFile, out MediaInfo ffprobeInfo, out MediaInfo mkvmergeInfo, out MediaInfo mediainfoInfo)
        {
            // Init
            ffprobeInfo = null;
            mkvmergeInfo = null;
            mediainfoInfo = null;

            // Read or create
            SidecarFile sidecarFile = new SidecarFile();
            if (!sidecarFile.GetMediaInfo(mediaFile))
                return false;

            // Assign
            ffprobeInfo = sidecarFile.FfProbeInfo;
            mkvmergeInfo = sidecarFile.MkvMergeInfo;
            mediainfoInfo = sidecarFile.MediaInfoInfo;

            return true;
        }

        public bool ReadSidecar(FileInfo mediaFile)
        {
            if (mediaFile == null)
                throw new ArgumentNullException(nameof(mediaFile));

            // Init
            Verified = false;
            FfProbeInfo = null;
            MkvMergeInfo = null;
            MediaInfoInfo = null;

            // Does the sidecar file exist
            string sidecarName = GetSidecarName(mediaFile);
            if (!File.Exists(sidecarName))
                return false;

            // Read the JSON from disk
            FileInfo sidecarFile = new FileInfo(sidecarName);
            if (!ReadSidecarJson(sidecarFile))
                return false;

            // Compare the schema version
            if (SidecarJson.SchemaVersion != SidecarFileJsonSchema.CurrentSchemaVersion)
            {
                Log.Logger.Error("Sidecar JSON schema mismatch : {SidecarJsonSchemaVersion} != {SidecarFileJsonSchemaCurrentSchemaVersion}, {Name}",
                                 SidecarJson.SchemaVersion, 
                                 SidecarFileJsonSchema.CurrentSchemaVersion, 
                                 sidecarFile.Name);
                return false;
            }

            // Compare the media modified time and file size
            mediaFile.Refresh();
            bool mismatch = false;
            if (mediaFile.LastWriteTimeUtc != SidecarJson.MediaLastWriteTimeUtc)
            {
                mismatch = true;
                Log.Logger.Warning("Sidecar LastWriteTimeUtc out of sync with media file : {SidecarJsonMediaLastWriteTimeUtc} != {MediaFileLastWriteTimeUtc} : {Name}", 
                                   SidecarJson.MediaLastWriteTimeUtc, 
                                   mediaFile.LastWriteTimeUtc, 
                                   sidecarFile.Name);
            }
            if (mediaFile.Length != SidecarJson.MediaLength)
            {
                mismatch = true;
                Log.Logger.Warning("Sidecar FileLength out of sync with media file : {SidecarJsonMediaLength} != {MediaFileLength} : {Name}", 
                                   SidecarJson.MediaLength, 
                                   mediaFile.Length, 
                                   sidecarFile.Name);
            }
            if (mismatch)
                return false;

            // Compare the tool versions
            if (!SidecarJson.FfProbeToolVersion.Equals(Tools.FfProbe.Info.Version, StringComparison.OrdinalIgnoreCase))
            {
                mismatch = true;
                Log.Logger.Warning("Sidecar FfProbe tool version out of date : {SidecarJsonFfProbeToolVersion} != {ToolsFfProbeInfoVersion} : {Name}", 
                                   SidecarJson.FfProbeToolVersion, 
                                   Tools.FfProbe.Info.Version, 
                                   sidecarFile.Name);
            }
            if (!SidecarJson.MkvMergeToolVersion.Equals(Tools.MkvMerge.Info.Version, StringComparison.OrdinalIgnoreCase))
            {
                mismatch = true;
                Log.Logger.Warning("Sidecar MkvMerge tool version out of date : {SidecarJsonMkvMergeToolVersion} != {ToolsMkvMergeInfoVersion} : {Name}", 
                                   SidecarJson.MkvMergeToolVersion, 
                                   Tools.MkvMerge.Info.Version, 
                                   sidecarFile.Name);
            }
            if (!SidecarJson.MediaInfoToolVersion.Equals(Tools.MediaInfo.Info.Version, StringComparison.OrdinalIgnoreCase))
            {
                mismatch = true;
                Log.Logger.Warning("Sidecar MediaInfo tool version out of date : {SidecarJsonMediaInfoToolVersion} != {ToolsMediaInfoVersion} : {Name}", 
                                   SidecarJson.MediaInfoToolVersion, 
                                   Tools.MediaInfo.Info.Version, 
                                   sidecarFile.Name);
            }
            if (mismatch && Program.Config.ProcessOptions.SidecarUpdateOnToolChange)
                return false;

            // Deserialize the tool data
            MediaInfo mediaInfoInfo = null;
            MediaInfo mkvMergeInfo = null;
            MediaInfo ffProbeInfo = null;
            if (!Tools.MediaInfo.GetMediaInfoFromXml(MediaInfoXml, out mediaInfoInfo) ||
                !Tools.MkvMerge.GetMkvInfoFromJson(MkvMergeInfoJson, out mkvMergeInfo) ||
                !Tools.FfProbe.GetFfProbeInfoFromJson(FfProbeInfoJson, out ffProbeInfo))
            {
                Log.Logger.Error("Failed to de-serialize tool data : {Name}", sidecarFile.Name);
                return false;
            }

            // Assign verified
            Verified = SidecarJson.Verified;

            // Assign mediainfo data
            FfProbeInfo = ffProbeInfo;
            MkvMergeInfo = mkvMergeInfo;
            MediaInfoInfo = mediaInfoInfo;

            return true;
        }

        public bool ReadSidecarJson(FileInfo sidecarFile)
        {
            if (sidecarFile == null)
                throw new ArgumentNullException(nameof(sidecarFile));

            try
            {
                // Read the sidecar file
                Log.Logger.Information("Reading media info from sidecar file : {Name}", sidecarFile.Name);
                SidecarJson = SidecarFileJsonSchema.FromJson(File.ReadAllText(sidecarFile.FullName));

                // Decompress the tool data
                FfProbeInfoJson = StringCompression.Decompress(SidecarJson.FfProbeInfoData);
                MkvMergeInfoJson = StringCompression.Decompress(SidecarJson.MkvMergeInfoData);
                MediaInfoXml = StringCompression.Decompress(SidecarJson.MediaInfoData);
            }
            catch (Exception e) when (Log.Logger.LogAndHandle(e, MethodBase.GetCurrentMethod().Name))
            {
                return false;
            }
            return true;
        }

        public bool CreateSidecar(FileInfo mediaFile)
        {
            if (mediaFile == null)
                throw new ArgumentNullException(nameof(mediaFile));

            // Read the tool data text
            Log.Logger.Information("Reading media info from tools : {Name}", mediaFile.Name);
            if (!Tools.MediaInfo.GetMediaInfoXml(mediaFile.FullName, out MediaInfoXml) ||
                !Tools.MkvMerge.GetMkvInfoJson(mediaFile.FullName, out MkvMergeInfoJson) ||
                !Tools.FfProbe.GetFfProbeInfoJson(mediaFile.FullName, out FfProbeInfoJson))
            {
                Log.Logger.Error("Failed to read media info : {Name}", mediaFile.Name);
                return false;
            }

            // Deserialize the tool data
            MediaInfo mediaInfoInfo = null;
            MediaInfo mkvMergeInfo = null;
            MediaInfo ffProbeInfo = null;
            if (!Tools.MediaInfo.GetMediaInfoFromXml(MediaInfoXml, out mediaInfoInfo) ||
                !Tools.MkvMerge.GetMkvInfoFromJson(MkvMergeInfoJson, out mkvMergeInfo) ||
                !Tools.FfProbe.GetFfProbeInfoFromJson(FfProbeInfoJson, out ffProbeInfo))
            {
                Log.Logger.Error("Failed to de-serialize tool data : {Name}", mediaFile.Name);
                return false;
            }

            // Assign the mediainfo data
            FfProbeInfo = ffProbeInfo;
            MkvMergeInfo = mkvMergeInfo;
            MediaInfoInfo = mediaInfoInfo;

            // Verify is externally assigned

            // Write the sidecar
            return WriteSidecarJson(mediaFile);
        }

        public bool WriteSidecarJson(FileInfo mediaFile)
        {
            if (mediaFile == null)
                throw new ArgumentNullException(nameof(mediaFile));

            // Delete the sidecar if it exists
            // string sidecarName = GetSidecarName(mediaFile);
            string sidecarName = Path.ChangeExtension(mediaFile.Name, SidecarExtension);
            string sidecarFullName = Path.ChangeExtension(mediaFile.FullName, SidecarExtension);
            if (File.Exists(sidecarFullName))
                File.Delete(sidecarFullName);

            // Refresh the media file info
            mediaFile.Refresh();

            // Create the sidecar json object
            SidecarJson = new SidecarFileJsonSchema
            {
                // Schema version
                SchemaVersion = SidecarFileJsonSchema.CurrentSchemaVersion,

                // Media file info
                MediaLastWriteTimeUtc = mediaFile.LastWriteTimeUtc,
                MediaLength = mediaFile.Length,

                // Tool version info
                FfProbeToolVersion = Tools.FfProbe.Info.Version,
                MkvMergeToolVersion = Tools.MkvMerge.Info.Version,
                MediaInfoToolVersion = Tools.MediaInfo.Info.Version,

                // Compressed tool info
                FfProbeInfoData = StringCompression.Compress(FfProbeInfoJson),
                MkvMergeInfoData = StringCompression.Compress(MkvMergeInfoJson),
                MediaInfoData = StringCompression.Compress(MediaInfoXml),

                // Verified flag
                Verified = Verified
            };

            try
            {
                // Write the json text to the sidecar file
                Log.Logger.Information("Writing media info to sidecar file : {Name}", sidecarName);
                File.WriteAllText(sidecarFullName, SidecarFileJsonSchema.ToJson(SidecarJson));
            }
            catch (Exception e) when (Log.Logger.LogAndHandle(e, MethodBase.GetCurrentMethod().Name))
            {
                return false;
            }
            return true;
        }

        public bool GetMediaInfo(FileInfo mediaFile)
        {
            return GetMediaInfo(mediaFile, false);
        }

        public bool GetMediaInfo(FileInfo mediaFile, bool refresh)
        {
            // Create a new sidecar
            if (refresh)
                return CreateSidecar(mediaFile);

            // Try to read the sidecar, else create a new sidecar
            return ReadSidecar(mediaFile) || CreateSidecar(mediaFile);
        }

        public MediaInfo GetMediaInfo(MediaTool.ToolType parser)
        {
            Debug.Assert(IsValid());

            return parser switch
            {
                MediaTool.ToolType.MediaInfo => MediaInfoInfo,
                MediaTool.ToolType.MkvMerge => MkvMergeInfo,
                MediaTool.ToolType.FfProbe => FfProbeInfo,
                _ => throw new NotImplementedException()
            };
        }

        public bool IsValid()
        {
            return FfProbeInfo != null &&
                   MkvMergeInfo != null &&
                   MediaInfoInfo != null;
        }

        public void WriteLine()
        {
            Log.Logger.Information("MediaInfoXml: {MediaInfoXml}", MediaInfoXml);
            Log.Logger.Information("MkvMergeInfoJson: {MkvMergeInfoJson}", MkvMergeInfoJson);
            Log.Logger.Information("FfProbeInfoJson: {FfProbeInfoJson}", FfProbeInfoJson);
            Log.Logger.Information("Verified: {Verified}", SidecarJson.Verified);
            Log.Logger.Information("MediaLastWriteTimeUtc: {MediaLastWriteTimeUtc}", SidecarJson.MediaLastWriteTimeUtc);
            Log.Logger.Information("MediaLength: {MediaLength}", SidecarJson.MediaLength);
            Log.Logger.Information("MediaInfoToolVersion: {MediaInfoToolVersion}", SidecarJson.MediaInfoToolVersion);
            Log.Logger.Information("MkvMergeToolVersion: {MkvMergeToolVersion}", SidecarJson.MkvMergeToolVersion);
            Log.Logger.Information("FfProbeToolVersion: {FfProbeToolVersion}", SidecarJson.FfProbeToolVersion);
        }

        public MediaInfo FfProbeInfo { get; set; }
        public MediaInfo MkvMergeInfo { get; set; }
        public MediaInfo MediaInfoInfo { get; set; }
        public bool Verified { get; set; }

        private string MediaInfoXml;
        private string MkvMergeInfoJson;
        private string FfProbeInfoJson;
        private SidecarFileJsonSchema SidecarJson;

        public const string SidecarExtension = @".PlexCleaner";
    }
}
