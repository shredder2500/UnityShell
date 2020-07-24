﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEditor;
using System.Diagnostics;
using System.Text;

namespace MS.Shell.Editor{
    public class EditorShell
    {

        public static string shellApp{
            get{
                #if UNITY_EDITOR_WIN
                string app = "cmd.exe";
                #elif UNITY_EDITOR_OSX
                string app = "bash";
                #else
                string app = "unsupport-platform";
                #endif
                return app;
            }
	    }

        #if UNITY_EDITOR_OSX
        private static char PATH_SPLIT_CHAR = ':';
        #elif UNITY_EDITOR_WIN
        private static char PATH_SPLIT_CHAR = ';';
        #else
        private static char PATH_SPLIT_CHAR = ':';
        #endif

        private static List<UnityAction> _queue = new List<UnityAction>();

        static EditorShell(){
            _queue = new List<UnityAction>();
            EditorApplication.update += OnUpdate;          
        }


        public static string JoinPaths(string[] paths){
            StringBuilder builder = new StringBuilder();
            for(var i = 0;i<paths.Length;i++){
                builder.Append(paths[i]);
                if(i < paths.Length -1 ){
                    builder.Append(PATH_SPLIT_CHAR);
                }
            }
            return builder.ToString();
        }

        public static string[] GetPaths(){
            var path = System.Environment.GetEnvironmentVariable("PATH");
            return path.Split(PATH_SPLIT_CHAR);
        }

        private static void OnUpdate(){
            while(_queue.Count > 0){
                lock(_queue){
                    var action = _queue[0];
                    try{
                        if(action != null){
                            action();
                        }
                    }catch(System.Exception e){
                        UnityEngine.Debug.LogException(e);
                    }finally{
                        _queue.RemoveAt(0);
                    }
                }
            }
        }

        private static void Enqueue(UnityAction action){
            lock(_queue){
                _queue.Add(action);
            }
        }
        public static Task Execute(string cmd,Options options = null){
            Task task = new Task();
            System.Threading.Tasks.Task.Run(() => {
                Process p = null;
                try{
                    ProcessStartInfo start = new ProcessStartInfo(shellApp);
                    #if UNITY_EDITOR_OSX
                    start.Arguments = "-c";
                    #elif UNITY_EDITOR_WIN
                    start.Arguments = "/c";
                    #endif

                    if(options == null){
                        options = new Options();
                    }
                    if(options.environmentVars != null){
                        foreach(var pair in options.environmentVars){
                            var value = System.Environment.ExpandEnvironmentVariables(pair.Value);
                            if (start.EnvironmentVariables.ContainsKey(pair.Key))
                            {
                                // UnityEngine.Debug.LogWarningFormat("Override EnvironmentVar, original = {0}, new = {1}",start.EnvironmentVariables[pair.Key],pair.Value);
                                start.EnvironmentVariables[pair.Key] = value;
                            }
                            else
                            {
                                start.EnvironmentVariables.Add(pair.Key, value);
                            }
                        }
                    }

                    start.Arguments += (" \"" + cmd + " \"");
                    start.CreateNoWindow = true;
                    start.ErrorDialog = true;
                    start.UseShellExecute = false;
                    start.WorkingDirectory = options.workDirectory == null ? "./":options.workDirectory;
                    start.RedirectStandardOutput = true;
                    start.RedirectStandardError = true;
                    start.RedirectStandardInput = true;
                    start.StandardOutputEncoding = options.encoding;
                    start.StandardErrorEncoding = options.encoding;
                    p = Process.Start(start);

                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                        UnityEngine.Debug.LogError(e.Data);
                    };
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                        UnityEngine.Debug.LogError(e.Data);
                    };
                    p.Exited += delegate(object sender, System.EventArgs e) {
                        UnityEngine.Debug.LogError(e.ToString());
                    };
                    
                    do{
                        if (options.cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        string line = p.StandardOutput.ReadLine();
                        if(line == null){
                            break;
                        }
                        line = line.Replace("\\","/");
                            
                        Enqueue(delegate() {
                            task.FeedLog(LogType.Log,line);
                        });

                    }while(true);
                    while(true){
                        if (options.cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        string error = p.StandardError.ReadLine();
                        if(string.IsNullOrEmpty(error)){
                            break;
                        }
                        Enqueue(delegate() {
                            task.FeedLog(LogType.Error,error);
                        });
                    }
                    if (options.cancellationToken.IsCancellationRequested)
                    {
                        p.Close();
                        Enqueue(() => {
                            task.FireDone(p.ExitCode);
                        });
                    }
                    else
                    {
                        p.WaitForExit();
                        var exitCode = p.ExitCode;
                        p.Close();
                        Enqueue(()=>{
                            task.FireDone(exitCode);
                        });
                    }
                }catch(System.Exception e){
                    UnityEngine.Debug.LogException(e);
                    if(p != null){
                        p.Close();
                    }
                    Enqueue(()=>{
                        task.FeedLog(LogType.Error,e.ToString());
                        task.FireDone(-1);
                    });
                }
            }, options?.cancellationToken ?? System.Threading.CancellationToken.None);
            return task;
        }

        public class Options{
            public System.Text.Encoding encoding = System.Text.Encoding.UTF8;
            public string workDirectory = "./";
            public Dictionary<string,string> environmentVars = new Dictionary<string,string>();
            public System.Threading.CancellationToken cancellationToken = System.Threading.CancellationToken.None;
        }


        public class Task{

            public event UnityAction<LogType,string> onLog;
            public event UnityAction<int> onExit;
            internal void FeedLog(LogType logType,string log){
                if(onLog != null){
                    onLog(logType,log);
                }
                if(logType == LogType.Error){
                    this.hasError = true;
                }
            }

            public bool hasError{
                get;private set;
            }

            public int exitCode{
                get;private set;
            }

            public bool isDone{
                get;private set;
            }

            internal void FireDone(int exitCode){
                this.exitCode = exitCode;
                this.isDone = true;
                if(onExit != null){
                    onExit(exitCode);
                }
            }
        }

        public enum LogType{
            Log,
            Error,
        }

    }
}
