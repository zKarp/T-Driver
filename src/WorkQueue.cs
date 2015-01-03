﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace TDriver {
    public class WorkCompleteEventArgs {
        public WorkCompleteEventArgs(Work work) {
            Subsection = work.SubSection;
            DocType = work.DocType;
            SentTo = work.DocObject.SendTo;
        }
        public String Subsection { get; private set; }
        public DocumentType DocType { get; private set; } 
        public String  SentTo { get; private set; }
    }

    public sealed class WorkQueue : IDisposable {
        /// <summary>
        ///     http://social.msdn.microsoft.com/forums/vstudio/en-US/500cb664-e2ca-4d76-88b9-0faab7e7c443/queuing-backgroundworker-tasks
        /// </summary>
        private readonly EventWaitHandle _doQWork = new EventWaitHandle(false, EventResetMode.ManualReset);

        private readonly Queue<Work> _workQueue = new Queue<Work>(50);
        private readonly Object _zLock = new object();
        private readonly WorkListConnection _wlConnection;

        private Thread _queueWorker;
        private Boolean _quitWork;

        public WorkQueue(string databaseFile) {
            _wlConnection = new WorkListConnection(databaseFile);
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Stops the Queue thread.
        /// </summary>
        public void StopQWorker() {
            _quitWork = true;
            _doQWork.Set();
            _queueWorker.Join(1000);
        }

        /// <summary>
        ///     Starts Queue thread.
        /// </summary>
        public void StartQWorker() {
            _queueWorker = new Thread(QThread) {IsBackground = true};
            _queueWorker.Start();
        }

        public void FoundFileCheck(string file, AP_Subsection fileSubsection) {
            //Create dpa from factory
            AP_Document doc = AP_Factory.Create(file, fileSubsection);
            if (doc == null) return;

            if (doc.IsValid) {
                Work work = WorkFactory.Create(doc, fileSubsection);
                if (work == null) return;
                AddToQueue(work);
            }

            else {
                Debug.WriteLine(doc.Account + " was skipped.");
            }
        }


        /// <summary>
        ///     Locks the queue and adds the new work.
        /// </summary>
        /// <param name="work"></param>
        private void AddToQueue(Work work) {
            lock (_zLock) {
                _workQueue.Enqueue(work);
            }
            _doQWork.Set();
        }

        /// <summary>
        ///     Checks all files from within the setting's directories, to see if they should be queued to be worked.
        /// </summary>
        public void QueueDirectory(string directoryToQueue, AP_Subsection subsection) {
            if (!Directory.Exists(directoryToQueue)) return; //Skip if the directory doesn't exist
            //Add all non-hidden files from the folder into an array.
            IEnumerable<FileInfo> existingDPAFiles =
                new DirectoryInfo(directoryToQueue).GetFiles().Where(x => (x.Attributes & FileAttributes.Hidden) == 0);
            if (existingDPAFiles.Any()) {
                foreach (FileInfo file in existingDPAFiles) {
                    FoundFileCheck(file.FullName, subsection);
                }
            }
        }


        /// <summary>
        ///     Background Thread function
        ///     Handles the work queue.
        /// </summary>
        private void QThread() {
            Debug.WriteLine("Thread Started.");

            do {
                //Wait for _doQWork event to start or _quitWork to stop.
                Debug.WriteLine("Waiting for work.");
                _doQWork.WaitOne(-1, false);
                if (_quitWork) {
                    break;
                }

                //Iterate through the work queue.
                Work dequeuedWork;
                do {
                    dequeuedWork = null;
                    //Lock the queue and grab next item.
                    lock (_zLock) {
                        if (_workQueue.Count > 0) {
                            dequeuedWork = _workQueue.Dequeue();
                            Debug.WriteLine(dequeuedWork.GetType() + " Found: " + dequeuedWork.DocObject.Document);
                        }
                    }

                    //Process if there is work to do.
                    if (dequeuedWork != null) {
                        Debug.WriteLine("Working...");
                        if (dequeuedWork.Process()) {
                            _wlConnection.Add(dequeuedWork.DocObject);
                            //Todo Handle failed database connection.
                            dequeuedWork.Move();
                            dequeuedWork.Completed = true;
                            Debug.WriteLine(dequeuedWork.GetType() + " Completed!");
                        }
                        else {
                            //TODO Remove this during deployment!!
                            _wlConnection.Add(dequeuedWork.DocObject);
                            Debug.WriteLine(dequeuedWork.GetType() + " Failed!");
                        }
                    }
                } while (dequeuedWork != null);

                //Verify the work queue is complete
                //Reset the _doQWork event if it is.
                lock (_zLock) {
                    if (_workQueue.Count == 0) {
                        _doQWork.Reset();
                    }
                }
            } while (true);
            //End thread and clean up.
            Debug.WriteLine("THREAD ENDED");
            _quitWork = false;
        }

        private void Dispose(bool disposing) {
            if (disposing) {
                // free managed resources
                _workQueue?.Clear(); //Check if null with null propergation.
            }
        }
    }
}