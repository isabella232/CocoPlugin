/***************************************************************************

Copyright (c) Microsoft Corporation. All rights reserved.
This code is licensed under the Visual Studio SDK license terms.
THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.

***************************************************************************/
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell.Interop;
using at.jku.ssw.Coco.VSPlugin.Language;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Threading;
using System.Runtime.InteropServices;

//NOTE: This class is from the VS SDK 2008 (with minor changes)
namespace at.jku.ssw.Coco.VSPlugin.Library {
    #region Delgates
    public delegate void TextLineChangeEvent(object sender, TextLineChange[] changes, int last);
    #endregion
    
    /// <summary>
    /// Inplementation of the service that build the information to expose to the symbols
    /// navigation tools (class view or object browser) from the grammar files inside a
    /// hierarchy.
    /// </summary>
    [System.Runtime.InteropServices.Guid(GuidList.guidLibraryString)]
    internal class CocoLibraryManager : IVsRunningDocTableEvents, IDisposable {

        #region LibraryTask
        /// <summary>
        /// Class storing the data about a parsing task on a grammar module.
        /// A module in Coco is a source file, so here we use the file name to
        /// identify it.
        /// </summary>
        private class LibraryTask {

            private string fileName;
            private string text;
            private ModuleId moduleId;

            public LibraryTask(string fileName, string text) {
                this.fileName = fileName;
                this.text = text;
            }

            public string FileName {
                get { return fileName; }
            }
            public ModuleId ModuleID {
                get { return moduleId; }
                set { moduleId = value; }
            }
            public string Text {
                get { return text; }
            }
        }
        #endregion

        #region Variables
        private IServiceProvider provider;
        private uint objectManagerCookie;
        private uint runningDocTableCookie;
        private Dictionary<uint, TextLineEventListener> documents;
        private Dictionary<IVsHierarchy, HierarchyListener> hierarchies;
        private Dictionary<ModuleId, LibraryNode> files;
        private Library library;
        private Thread parseThread;
        private ManualResetEvent requestPresent;
        private ManualResetEvent shutDownStarted;
        private Queue<LibraryTask> requests;
        #endregion

        #region Constructor
        public CocoLibraryManager(IServiceProvider provider) {
            documents = new Dictionary<uint, TextLineEventListener>();
            hierarchies = new Dictionary<IVsHierarchy, HierarchyListener>();
            library = new Library(GuidList.guidLibrary);
            library.LibraryCapabilities = (_LIB_FLAGS2)_LIB_FLAGS.LF_PROJECT;
            library.LibraryCapabilities |= (_LIB_FLAGS2.LF_SHOWFULLNAMESINFINDSYMBOLRESULTS | _LIB_FLAGS2.LF_SUPPORTSLISTREFERENCES | _LIB_FLAGS2.LF_SUPPORTSFILTERING);
            files = new Dictionary<ModuleId, LibraryNode>();
            this.provider = provider;
            requests = new Queue<LibraryTask>();
            requestPresent = new ManualResetEvent(false);
            shutDownStarted = new ManualResetEvent(false);
            parseThread = new Thread(new ThreadStart(ParseThread));
            parseThread.Start();
        }
        #endregion

        #region Properties
        /// <summary>
        /// Gets the library.
        /// </summary>
        public Library Library {
            get {
                return library;
            }
        }
        #endregion

        #region RTDEvents
        private void RegisterForRDTEvents() {
            if (0 != runningDocTableCookie) {
                return;
            }
            IVsRunningDocumentTable rdt = provider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (null != rdt) {
                // Do not throw here in case of error, simply skip the registration.
                rdt.AdviseRunningDocTableEvents(this, out runningDocTableCookie);
            }
        }
        private void UnregisterRDTEvents() {
            if (0 == runningDocTableCookie) {
                return;
            }
            IVsRunningDocumentTable rdt = provider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (null != rdt) {
                // Do not throw in case of error.
                rdt.UnadviseRunningDocTableEvents(runningDocTableCookie);
            }
            runningDocTableCookie = 0;
        }
        #endregion

        #region IDisposable Members
        public void Dispose() {
            // Make sure that the parse thread can exit.
            if (null != shutDownStarted) {
                shutDownStarted.Set();
            }
            if ((null != parseThread) && parseThread.IsAlive) {
                parseThread.Join(500);
                if (parseThread.IsAlive) {
                    parseThread.Abort();
                }
                parseThread = null;
            }

            requests.Clear();

            // Dispose all the listeners.
            foreach (HierarchyListener listener in hierarchies.Values) {
                listener.Dispose();
            }
            hierarchies.Clear();

            foreach (TextLineEventListener textListener in documents.Values) {
                textListener.Dispose();
            }
            documents.Clear();

            // Remove this library from the object manager.
            if (0 != objectManagerCookie) {
                IVsObjectManager2 mgr = provider.GetService(typeof(SVsObjectManager)) as IVsObjectManager2;
                if (null != mgr) {
                    mgr.UnregisterLibrary(objectManagerCookie);
                }
                objectManagerCookie = 0;
            }

            // Unregister this object from the RDT events.
            UnregisterRDTEvents();

            // Dispose the events used to syncronize the threads.
            if (null != requestPresent) {
                requestPresent.Close();
                requestPresent = null;
            }
            if (null != shutDownStarted) {
                shutDownStarted.Close();
                shutDownStarted = null;
            }
        }
        #endregion

        #region Registration Methods
        public void RegisterHierarchy(IVsHierarchy hierarchy) {
            if ((null == hierarchy) || hierarchies.ContainsKey(hierarchy)) {
                return;
            }
            if (0 == objectManagerCookie) {
                IVsObjectManager2 objManager = provider.GetService(typeof(SVsObjectManager)) as IVsObjectManager2;
                if (null == objManager) {
                    return;
                }
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(
                    objManager.RegisterSimpleLibrary(library, out objectManagerCookie));
            }
            HierarchyListener listener = new HierarchyListener(hierarchy);
            listener.OnAddItem += new EventHandler<HierarchyEventArgs>(OnNewFile);
            listener.OnDeleteItem += new EventHandler<HierarchyEventArgs>(OnDeleteFile);
            listener.StartListening(true);
            hierarchies.Add(hierarchy, listener);
            RegisterForRDTEvents();
        }

        public void UnregisterHierarchy(IVsHierarchy hierarchy) {
            if ((null == hierarchy) || !hierarchies.ContainsKey(hierarchy)) {
                return;
            }
            HierarchyListener listener = hierarchies[hierarchy];
            if (null != listener) {
                listener.Dispose();
            }
            hierarchies.Remove(hierarchy);
            if (0 == hierarchies.Count) {
                UnregisterRDTEvents();
            }
            lock (files) {
                ModuleId[] keys = new ModuleId[files.Keys.Count];
                files.Keys.CopyTo(keys, 0);
                foreach (ModuleId id in keys) {
                    if (hierarchy.Equals(id.Hierarchy)) {
                        library.RemoveNode(files[id]);
                        files.Remove(id);
                    }
                }
            }
            // Remove the document listeners.
            uint[] docKeys = new uint[documents.Keys.Count];
            documents.Keys.CopyTo(docKeys, 0);
            foreach (uint id in docKeys) {
                TextLineEventListener docListener = documents[id];
                if (hierarchy.Equals(docListener.FileID.Hierarchy)) {
                    documents.Remove(id);
                    docListener.Dispose();
                }
            }
        }

        public void RegisterLineChangeHandler(uint document,
            TextLineChangeEvent lineChanged, Action<IVsTextLines> onIdle) {
            documents[document].OnFileChangedImmediate += delegate(object sender, TextLineChange[] changes, int fLast) {
                lineChanged(sender, changes, fLast);
            };
            documents[document].OnFileChanged += delegate(object sender, HierarchyEventArgs args) {
                onIdle(args.TextBuffer);
            };
        }

        private void CreateParseRequest(string file, string text, ModuleId id) {
            LibraryTask task = new LibraryTask(file, text);
            task.ModuleID = id;
            lock (requests) {
                requests.Enqueue(task);
            }
            requestPresent.Set();
        }

        public void OnIdle() {
            foreach (TextLineEventListener listener in documents.Values) {
                listener.OnIdle();
            }
        }
        #endregion

        #region Parse Thread
        /// <summary>
        /// Main function of the parsing thread.
        /// This function waits on the queue of the parsing requests and build the parsing tree for
        /// a specific file. The resulting tree is built using LibraryNode objects so that it can
        /// be used inside the class view or object browser.
        /// </summary>
        private void ParseThread() {
            const int waitTimeout = 500;
            // Define the array of events this function is interest in.
            WaitHandle[] eventsToWait = new WaitHandle[] { requestPresent, shutDownStarted };
            // Execute the tasks.
            while (true) {
                // Wait for a task or a shutdown request.
                int waitResult = WaitHandle.WaitAny(eventsToWait, waitTimeout, false);
                if (1 == waitResult) {
                    // The shutdown of this component is started, so exit the thread.
                    return;
                }
                LibraryTask task = null;
                lock (requests) {
                    if (0 != requests.Count) {
                        task = requests.Dequeue();
                    }
                    if (0 == requests.Count) {
                        requestPresent.Reset();
                    }
                }
                if (null == task) {
                    continue;
                }

                string sourceText = task.Text;
                if (sourceText == null) {
                    //should not happen, but to be robust
                    if (!System.IO.File.Exists(task.FileName))
                        continue;

                    //read file content as string
                    try {
                        using (System.IO.StreamReader sr = System.IO.File.OpenText(task.FileName)) {
                            sourceText = sr.ReadToEnd();
                        }
                    }
                    catch (Exception ex) {
                        System.Diagnostics.Trace.WriteLine(String.Format("Error when parsing file '{0}': {1}\n{2}", task.FileName, ex.Message, ex.StackTrace));
                        continue;
                    }
                }
                
                if (string.IsNullOrEmpty(sourceText))
                    continue;
                                
                CocoParserProxy.CocoParseResult result = CocoParserProxy.Parse(sourceText, task.FileName);
                                
                //maybe use coco-library entry (to avoid not-implemented exception in librarynode.getcategory2)
                CocoLibraryNode module = new CocoLibraryNode(
                        System.IO.Path.GetFileName(task.FileName),
                        1,
                        1,
                        1,
                        task.ModuleID.Hierarchy,
                        task.ModuleID.ItemID,
                        LibraryNode.LibraryNodeType.Classes);

                
                CreateLibraryEntry(module, result, task.ModuleID);
                
                if (null != task.ModuleID) {
                    LibraryNode previousItem = null;
                    lock (files) {
                        if (files.TryGetValue(task.ModuleID, out previousItem)) {
                            files.Remove(task.ModuleID);
                        }
                    }
                    library.RemoveNode(previousItem);
                }
                library.AddNode(module);
                if (null != task.ModuleID) {
                    lock (files) {
                        files.Add(task.ModuleID, module);
                    }
                }                
            }
        }

        private void CreateLibraryEntry(LibraryNode parent, CocoParserProxy.CocoParseResult result, ModuleId moduleId) {
            CreateLibraryEntry(parent, moduleId, result.CharSets, result.References);
            CreateLibraryEntry(parent, moduleId, result.Tokens, result.References);
            CreateLibraryEntry(parent, moduleId, result.Productions, result.References);
        }

        private void CreateLibraryEntry(LibraryNode parent, ModuleId moduleId, IEnumerable<AddTokenInfo> tokens, IDictionary<string, List<AddTokenInfo>> references) {
            foreach (AddTokenInfo info in tokens) {
                int length = info.Name == null ? 0 : info.Name.Length;
                CocoLibraryNode entry = new CocoLibraryNode(info.Name, info.Line, info.Col, info.Col + length, moduleId.Hierarchy, moduleId.ItemID, LibraryNode.LibraryNodeType.Members);
                parent.AddNode(entry);
                if (references.ContainsKey(info.Name)) {
                    foreach(AddTokenInfo refInfo in references[info.Name]) {
                        if (refInfo.Line == info.Line && refInfo.Col == info.Col)
                            continue; //sometimes the definition is also tracked as reference (only with tokendecls, but changes to original parser would be necessary to avoid this) -> simply skip it here

                        //it is important that the name is starts with the same name as the definition, followed by a whitespace (easy search-algorithm)
                        string refName = String.Format("{0} ({1}, {2}): {3}", refInfo.Name, refInfo.Line, refInfo.Col, refInfo.AdditionalInfo);
                        CocoLibraryNode reference = new CocoLibraryNode(refName, refInfo.Line, refInfo.Col, refInfo.Col + length, moduleId.Hierarchy, moduleId.ItemID, LibraryNode.LibraryNodeType.References);                        
                        reference.Visible = false;
                        parent.AddNode(reference);
                    }
                }
            }
        }
                
        #endregion
        
        #region Hierarchy Events
        private void OnNewFile(object sender, HierarchyEventArgs args) {
            IVsHierarchy hierarchy = sender as IVsHierarchy;
            if (null == hierarchy) {
                return;
            }
            string fileText = null;
            if (null != args.TextBuffer) {
                int lastLine;
                int lastIndex;
                int hr = args.TextBuffer.GetLastLineIndex(out lastLine, out lastIndex);
                if (Microsoft.VisualStudio.ErrorHandler.Failed(hr)) {
                    return;
                }
                hr = args.TextBuffer.GetLineText(0, 0, lastLine, lastIndex, out fileText);
                if (Microsoft.VisualStudio.ErrorHandler.Failed(hr)) {
                    return;
                }
            }
            CreateParseRequest(args.CanonicalName, fileText, new ModuleId(hierarchy, args.ItemID));
        }

        private void OnDeleteFile(object sender, HierarchyEventArgs args) {
            IVsHierarchy hierarchy = sender as IVsHierarchy;
            if (null == hierarchy) {
                return;
            }
            ModuleId id = new ModuleId(hierarchy, args.ItemID);
            LibraryNode node = null;
            lock (files) {
                if (files.TryGetValue(id, out node)) {
                    files.Remove(id);
                }
            }
            if (null != node) {
                library.RemoveNode(node);
            }
        }
        #endregion

        #region IVsRunningDocTableEvents Members

        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) {
            if ((grfAttribs & (uint)(__VSRDTATTRIB.RDTA_MkDocument)) == (uint)__VSRDTATTRIB.RDTA_MkDocument) {
                IVsRunningDocumentTable rdt = provider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                if (rdt != null) {
                    uint flags, readLocks, editLocks, itemid;
                    IVsHierarchy hier;
                    IntPtr docData = IntPtr.Zero;
                    string moniker;
                    int hr;
                    try {
                        hr = rdt.GetDocumentInfo(docCookie, out flags, out readLocks, out editLocks, out moniker, out hier, out itemid, out docData);
                        TextLineEventListener listner;
                        if (documents.TryGetValue(docCookie, out listner)) {
                            listner.FileName = moniker;
                        }
                    }
                    finally {
                        if (IntPtr.Zero != docData) {
                            Marshal.Release(docData);
                        }
                    }
                }
            }
            return VSConstants.S_OK;
        }

        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) {
            return VSConstants.S_OK;
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) {
            return VSConstants.S_OK;
        }

        public int OnAfterSave(uint docCookie) {
            lock (documents) {
                if (documents.ContainsKey(docCookie)) {
                    documents[docCookie].OnIdle(); //saved documents will be parsed immediatly (with a max delay of 500ms)
                }
            }
            return VSConstants.S_OK;
        }

        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) {
            // Check if this document is in the list of the documents.
            if (documents.ContainsKey(docCookie)) {
                return VSConstants.S_OK;
            }
            // Get the information about this document from the RDT.
            IVsRunningDocumentTable rdt = provider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (null != rdt) {
                // Note that here we don't want to throw in case of error.
                uint flags;
                uint readLocks;
                uint writeLoks;
                string documentMoniker;
                IVsHierarchy hierarchy;
                uint itemId;
                IntPtr unkDocData;
                int hr = rdt.GetDocumentInfo(docCookie, out flags, out readLocks, out writeLoks,
                                             out documentMoniker, out hierarchy, out itemId, out unkDocData);
                try {
                    if (Microsoft.VisualStudio.ErrorHandler.Failed(hr) || (IntPtr.Zero == unkDocData)) {
                        return VSConstants.S_OK;
                    }
                    // Check if the herarchy is one of the hierarchies this service is monitoring.
                    if (!hierarchies.ContainsKey(hierarchy)) {
                        // This hierarchy is not monitored, we can exit now.
                        return VSConstants.S_OK;
                    }

                    // Check the extension of the file to see if a listener is required.
                    string extension = System.IO.Path.GetExtension(documentMoniker);
                    if (0 != string.Compare(extension, CocoLanguageService.EXTENSION, StringComparison.OrdinalIgnoreCase)) {
                        return VSConstants.S_OK;
                    }

                    // Create the module id for this document.
                    ModuleId docId = new ModuleId(hierarchy, itemId);

                    // Try to get the text buffer.
                    IVsTextLines buffer = Marshal.GetObjectForIUnknown(unkDocData) as IVsTextLines;

                    // Create the listener.
                    TextLineEventListener listener = new TextLineEventListener(buffer, documentMoniker, docId);
                    // Set the event handler for the change event. Note that there is no difference
                    // between the AddFile and FileChanged operation, so we can use the same handler.
                    listener.OnFileChanged += new EventHandler<HierarchyEventArgs>(OnNewFile);
                    // Add the listener to the dictionary, so we will not create it anymore.
                    documents.Add(docCookie, listener);
                }
                finally {
                    if (IntPtr.Zero != unkDocData) {
                        Marshal.Release(unkDocData);
                    }
                }
            }
            // Always return success.
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) {
            if ((0 != dwEditLocksRemaining) || (0 != dwReadLocksRemaining)) {
                return VSConstants.S_OK;
            }
            TextLineEventListener listener;
            if (!documents.TryGetValue(docCookie, out listener) || (null == listener)) {
                return VSConstants.S_OK;
            }
            using (listener) {
                documents.Remove(docCookie);
                // Now make sure that the information about this file are up to date (e.g. it is
                // possible that Class View shows something strange if the file was closed without
                // saving the changes).
                HierarchyEventArgs args = new HierarchyEventArgs(listener.FileID.ItemID, listener.FileName);
                OnNewFile(listener.FileID.Hierarchy, args);
            }
            return VSConstants.S_OK;
        }

        #endregion        
    }
}
