﻿//  Copyright 2016, 2017 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace SandboxAnalysisUtils
{
    /// <summary>
    /// Contains information about a manifest file.
    /// </summary>
    public class ExecutableManifest
    {
        const string MANIFEST_ASMV1_NS = "urn:schemas-microsoft-com:asm.v1";
        const string MANIFEST_ASMV3_NS = "urn:schemas-microsoft-com:asm.v3";
        const string MANIFEST_WS_NS = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";
        
        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern bool EnumResourceTypes(IntPtr hModule, EnumResTypeProc lpEnumFunc, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool EnumResTypeProc(IntPtr hModule, IntPtr lpszType, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool EnumResNameProcDelegate(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

        enum ResType
        {
            CURSOR = 1,
            BITMAP = 2,
            ICON = 3,
            MENU = 4,
            DIALOG = 5,
            STRING = 6,
            FONTDIR = 7,
            FONT = 8,
            ACCELERATOR = 9,
            RCDATA = 10,
            MESSAGETABLE = 11,
            GROUP_CURSOR = 12,
            GROUP_ICON = 14,
            VERSION = 16,
            DLGINCLUDE = 17,
            PLUGPLAY = 19,
            VXD = 20,
            ANICURSOR = 21,
            ANIICON = 22,
            HTML = 23,
            MANIFEST = 24
        }

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern bool EnumResourceNames(SafeLoadLibraryHandle hModule, IntPtr lpszType, 
            EnumResNameProcDelegate lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadResource(SafeLoadLibraryHandle hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int SizeofResource(SafeLoadLibraryHandle hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindResource(SafeLoadLibraryHandle hModule, IntPtr lpName, IntPtr lpType);

        private static string FormatTypeName(IntPtr p)
        {
            if (p.ToInt64() < 0x10000)
            {
                return p.ToString();
            }
            else
            {
                return Marshal.PtrToStringUni(p);
            }
        }

        private static XmlNamespaceManager CreateNSMgr(XmlNameTable nt)
        {
            XmlNamespaceManager nsmgr = new XmlNamespaceManager(nt);

            nsmgr.AddNamespace("asmv1", MANIFEST_ASMV1_NS);
            nsmgr.AddNamespace("asmv3", MANIFEST_ASMV3_NS);
            nsmgr.AddNamespace("ws", MANIFEST_WS_NS);

            return nsmgr;
        }

        private static XmlNode GetNode(XmlDocument doc, string path)
        {
            return doc.SelectSingleNode(path, CreateNSMgr(doc.NameTable));
        }

        private static bool GetUiAccess(XmlDocument doc)
        {
            XmlNode node = GetNode(doc, "/asmv1:assembly/asmv3:trustInfo/asmv3:security/asmv3:requestedPrivileges/asmv3:requestedExecutionLevel/@uiAccess");

            if (node != null)
            {
                bool ret;

                if (bool.TryParse(node.Value, out ret))
                {
                    return ret;
                }
            }

            return false;
        }

        private static string GetExecutionLevel(XmlDocument doc)
        {
            XmlNode node = GetNode(doc, "/asmv1:assembly/asmv3:trustInfo/asmv3:security/asmv3:requestedPrivileges/asmv3:requestedExecutionLevel/@level");

            if (node != null)
            {
                return node.Value;
            }

            return "asInvoker";
        }

        private static bool GetAutoElevate(XmlDocument doc)
        {
            bool ret = false;
            XmlNode node = GetNode(doc, "/asmv1:assembly/asmv3:application/asmv3:windowsSettings/ws:autoElevate");

            if (node != null)
            {
                if (!bool.TryParse(node.InnerText.Trim(), out ret))
                {
                    ret = false;
                }
            }
            return ret;
        }

        private static XmlDocument LoadDocument(MemoryStream stm)
        {
            XmlDocument doc = new XmlDocument();
            XmlParserContext parse_context =
                new XmlParserContext(null, CreateNSMgr(new NameTable()), null, XmlSpace.Default);
            XmlReader reader = XmlReader.Create(stm, null, parse_context);
            doc.Load(reader);
            return doc;
        }
        
        internal ExecutableManifest(SafeLoadLibraryHandle hModule, string fullpath, IntPtr hName)
        {
            FullPath = fullpath;

            IntPtr hResHandle = FindResource(hModule, hName, new IntPtr((int)ResType.MANIFEST));
            if (hResHandle == IntPtr.Zero)
            {
                throw new ArgumentException("Can't find manifest resource");
            }

            IntPtr hResource = LoadResource(hModule, hResHandle);
            IntPtr buf = LockResource(hResource);
            int size = SizeofResource(hModule, hResHandle);

            if (size <= 0)
            {
                throw new ArgumentException("Invalid manifest size");
            }
            byte[] manifest = new byte[size];

            Marshal.Copy(buf, manifest, 0, size);
            MemoryStream stm = new MemoryStream(manifest);
            try
            {
                XmlDocument doc = LoadDocument(stm);

                UiAccess = GetUiAccess(doc);
                AutoElevate = GetAutoElevate(doc);
                ExecutionLevel = GetExecutionLevel(doc);
                       
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Indent = true;
                settings.OmitXmlDeclaration = true;
                settings.NewLineOnAttributes = true;
                StringWriter string_writer = new StringWriter();
                XmlWriter writer = XmlWriter.Create(string_writer, settings);
                doc.Save(writer);
                ManifestXml = string_writer.ToString();
            }
            catch (XmlException)
            {
                ParseError = true;
                ManifestXml = Encoding.UTF8.GetString(stm.ToArray());
            }            
        }

        public bool ParseError { get; private set; }
        public string FullPath { get; private set; }
        public string Name
        {
            get
            {
                return Path.GetFileName(FullPath);
            }
        }
        public bool UiAccess { get; private set; }
        public string ExecutionLevel { get; private set; }
        public bool AutoElevate { get; private set; }
        public string ManifestXml { get; private set; }

        public static IEnumerable<ExecutableManifest> GetManifests(string filename)
        {
            string fullpath = Path.GetFullPath(filename);
            
            using (SafeLoadLibraryHandle library =
                SafeLoadLibraryHandle.LoadLibrary(fullpath, LoadLibraryFlags.LoadLibraryAsImageResource | LoadLibraryFlags.LoadLibraryAsDataFile))
            {
                List<ExecutableManifest> manifests = new List<ExecutableManifest>();

                EnumResourceNames(library, new IntPtr((int)ResType.MANIFEST), (a, b, c, d) =>
                {
                    try
                    {
                        manifests.Add(new ExecutableManifest(library, fullpath, c));
                    }
                    catch (Win32Exception)
                    {
                    }
                    catch (ArgumentException)
                    {
                    }
                    return true;
                }, IntPtr.Zero);

                return manifests;
            }
        }
    }
}
