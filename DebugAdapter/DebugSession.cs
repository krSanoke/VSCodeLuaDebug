﻿// Original work by:
/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// Modified by:
/*---------------------------------------------------------------------------------------------
*  Copyright (c) NEXON Korea Corporation. All rights reserved.
*  Licensed under the MIT License. See License.txt in the project root for license information.
*--------------------------------------------------------------------------------------------*/

using GiderosPlayerRemote;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace VSCodeDebug
{
    public class DebugSession : ICDPListener, IDebuggeeListener
    {
        public ICDPSender toVSCode;
        public IDebuggeeSender toDebugee;
        private Process process;

        public DebugSession()
        {
            Program.WaitingUI.SetLabelText(
                "Waiting for commands from Visual Studio Code...");
        }

        void ICDPListener.FromVSCode(string command, int seq, dynamic args, string reqText)
        {
            MessageBox.OK(reqText);

            if (args == null)
            {
                args = new { };
            }

            try
            {
                switch (command)
                {
                    case "initialize":
                        Initialize(command, seq, args);
                        break;

                    case "launch":
                        Launch(command, seq, args);
                        break;

                    case "attach":
                        Attach(command, seq, args);
                        break;

                    case "disconnect":
                        Disconnect(command, seq, args);
                        break;

                    case "next":
                    case "continue":
                    case "stepIn":
                    case "stepOut":
                    case "stackTrace":
                    case "scopes":
                    case "variables":
                    case "threads":
                    case "setBreakpoints":
                    case "configurationDone":
                    case "evaluate":
                    case "pause":
                        toDebugee.Send(reqText);
                        break;

                    case "source":
                        SendErrorResponse(command, seq, 1020, "command not supported: " + command);
                        break;

                    default:
                        SendErrorResponse(command, seq, 1014, "unrecognized request: {_request}", new { _request = command });
                        break;
                }
            }
            catch (Exception e)
            {
                MessageBox.WTF(e.ToString());
                SendErrorResponse(command, seq, 1104, "error while processing request '{_request}' (exception: {_exception})", new { _request = command, _exception = e.Message });
                Environment.Exit(1);
            }
        }

        void IDebuggeeListener.FromDebuggee(byte[] json)
        {
            toVSCode.SendJSONEncodedMessage(json);
        }

        public void SendResponse(string command, int seq, dynamic body)
        {
            var response = new Response(command, seq);
            if (body != null)
            {
                response.SetBody(body);
            }
            toVSCode.SendMessage(response);
        }

        public void SendErrorResponse(string command, int seq, int id, string format, dynamic arguments = null, bool user = true, bool telemetry = false)
        {
            var response = new Response(command, seq);
            var msg = new Message(id, format, arguments, user, telemetry);
            var message = Utilities.ExpandVariables(msg.format, msg.variables);
            response.SetErrorBody(message, new ErrorResponseBody(msg));
            toVSCode.SendMessage(response);
        }

        void Disconnect(string command, int seq, dynamic arguments)
        {
            if (process != null)
            {
                try
                {
                    process.Kill();
                }
                catch(Exception)
                {
                    // 정상 종료하면 이쪽 경로로 들어온다.
                }
                process = null;
            }

            SendResponse(command, seq, null);
            toVSCode.Stop();
        }

        void Initialize(string command, int seq, dynamic args)
        {
            SendResponse(command, seq, new Capabilities()
            {
                supportsConfigurationDoneRequest = true,
                supportsFunctionBreakpoints = false,
                supportsConditionalBreakpoints = false,
                supportsEvaluateForHovers = false,
                exceptionBreakpointFilters = new dynamic[0]
            });
        }

        void Launch(string command, int seq, dynamic args)
        {
            // 런치 전에 디버기가 접속할 수 있게 포트를 먼저 열어야 한다.
            var listener = PrepareForDebuggee(command, seq, args);

            string gprojPath = args.gprojPath;
            if (gprojPath == null)
            {
                //--------------------------------
                // validate argument 'executable'
                var runtimeExecutable = (string)args.executable;
                if (runtimeExecutable == null) { runtimeExecutable = ""; }

                runtimeExecutable = runtimeExecutable.Trim();
                if (runtimeExecutable.Length == 0)
                {
                    SendErrorResponse(command, seq, 3005, "Property 'executable' is empty.");
                    return;
                }
                if (!File.Exists(runtimeExecutable))
                {
                    SendErrorResponse(command, seq, 3006, "Runtime executable '{path}' does not exist.", new { path = runtimeExecutable });
                    return;
                }

                //--------------------------------
                // validate argument 'workingDirectory'
                var workingDirectory = ReadWorkingDirectory(command, seq, args);
                if (workingDirectory == null) { return; }

                //--------------------------------
                var arguments = (string)args.arguments;
                if (arguments == null) { arguments = ""; }

                process = new Process();
                process.StartInfo.CreateNoWindow = false;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.WorkingDirectory = workingDirectory;
                process.StartInfo.FileName = runtimeExecutable;
                process.StartInfo.Arguments = arguments;

                process.EnableRaisingEvents = true;
                process.Exited += (object sender, EventArgs e) =>
                {
                    toVSCode.SendMessage(new TerminatedEvent());
                };

                var cmd = string.Format("{0} {1}\n", runtimeExecutable, arguments);
                toVSCode.SendOutput("console", cmd);

                try
                {
                    process.Start();
                }
                catch (Exception e)
                {
                    SendErrorResponse(command, seq, 3012, "Can't launch terminal ({reason}).", new { reason = e.Message });
                    return;
                }
            }
            else
            {
                var rc = new RemoteController();

                var connectStartedAt = DateTime.Now;
                bool alreadyLaunched = false;
                while (!rc.TryStart("127.0.0.1", 15000, gprojPath, GiderosRemoteControllerLogger))
                {
                    if (DateTime.Now - connectStartedAt > TimeSpan.FromSeconds(10))
                    {
                        SendErrorResponse(command, seq, 3012, "Can't connect to GiderosPlayer.", new { });
                        return;
                    }
                    else if (alreadyLaunched)
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                    else
                    {
                        try
                        {
                            var giderosPath = (string)args.giderosPath;
                            process = new Process();
                            process.StartInfo.UseShellExecute = true;
                            process.StartInfo.CreateNoWindow = true;
                            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                            process.StartInfo.WorkingDirectory = giderosPath;

                            // I don't know why this fix keeps GiderosPlayer.exe running
                            // after DebugAdapter stops.
                            // And I don't want to know..
                            process.StartInfo.FileName = "cmd.exe";
                            process.StartInfo.Arguments = "/c \"start GiderosPlayer.exe\"";
                            process.Start();

                            Program.WaitingUI.SetLabelText(
                                "Launching " + process.StartInfo.FileName + " " +
                                process.StartInfo.Arguments + "...");
                        }
                        catch (Exception e)
                        {
                            SendErrorResponse(command, seq, 3012, "Can't launch GiderosPlayer ({reason}).", new { reason = e.Message });
                            return;
                        }
                        alreadyLaunched = true;
                    }
                }

                new System.Threading.Thread(rc.ReadLoop).Start();
            }

            AcceptDebuggee(command, seq, args, listener);
        }

        void Attach(string command, int seq, dynamic args)
        {
            var listener = PrepareForDebuggee(command, seq, args);
            AcceptDebuggee(command, seq, args, listener);
        }

        TcpListener PrepareForDebuggee(string command, int seq, dynamic args)
        {
            IPAddress listenAddr = (bool)args.listenPublicly
                ? IPAddress.Any
                : IPAddress.Parse("127.0.0.1");
            int port = (int)args.listenPort;

            TcpListener listener = new TcpListener(listenAddr, port);
            listener.Start();
            return listener;
        }

        void AcceptDebuggee(string command, int seq, dynamic args, TcpListener listener)
        {
            var workingDirectory = ReadWorkingDirectory(command, seq, args);
            if (workingDirectory == null) { return; }

            var encodingName = (string)args.encoding;
            Encoding encoding;
            if (encodingName != null)
            {
                int codepage;
                if (int.TryParse(encodingName, out codepage))
                {
                    encoding = Encoding.GetEncoding(codepage);
                }
                else
                {
                    encoding = Encoding.GetEncoding(encodingName);
                }
            }
            else
            {
                encoding = Encoding.UTF8;
            }

            Program.WaitingUI.SetLabelText(
                "Waiting for debugee at TCP " +
                listener.LocalEndpoint.ToString() + "...");

            var clientSocket = listener.AcceptSocket(); // blocked here
            listener.Stop();
            Program.WaitingUI.Hide();
            var ncom = new DebuggeeProtocol(
                this,
                new NetworkStream(clientSocket),
                encoding);
            this.toDebugee = ncom;

            var welcome = new
            {
                command = "welcome",
                sourceBasePath = workingDirectory
            };
            toDebugee.Send(JsonConvert.SerializeObject(welcome));

            ncom.StartThread();
            SendResponse(command, seq, null);

            toVSCode.SendMessage(new InitializedEvent());
        }

        string ReadWorkingDirectory(string command, int seq, dynamic args)
        {
            var workingDirectory = (string)args.workingDirectory;
            if (workingDirectory == null) { workingDirectory = ""; }

            workingDirectory = workingDirectory.Trim();
            if (workingDirectory.Length == 0)
            {
                SendErrorResponse(command, seq, 3003, "Property 'cwd' is empty.");
                return null;
            }
            if (!Directory.Exists(workingDirectory))
            {
                SendErrorResponse(command, seq, 3004, "Working directory '{path}' does not exist.", new { path = workingDirectory });
                return null;
            }

            return workingDirectory;
        }

        public void DebugeeHasGone()
        {
            toVSCode.SendMessage(new TerminatedEvent());
        }

        // ATTENTION: Called from different thread.
        string stdoutBuffer = "";
        void GiderosRemoteControllerLogger(LogType logType, string content)
        {
            switch (logType)
            {
                case LogType.Info:
                    toVSCode.SendOutput("console", content);
                    break;

                case LogType.PlayerOutput:
                    Filter(content);
                    // Gideros sends '\n' as seperate packet,
                    // and VS Code adds linefeed to the end of each output message.
                    if (content == "\n")
                    {
                        toVSCode.SendOutput("stdout", stdoutBuffer);
                        stdoutBuffer = "";
                    }
                    else
                    {
                        stdoutBuffer += content;
                    }
                    break;

                case LogType.Warning:
                    toVSCode.SendOutput("stderr", content);
                    break;
            }
        }

        protected static readonly Regex errorMatcher = new Regex(@"^([^:\n\r]+):(\d+): ");
        void Filter(string content)
        {
            Match m = errorMatcher.Match(content);
            if (!m.Success) { return; }

            string file = m.Groups[1].ToString();
            int line = int.Parse(m.Groups[2].ToString());

            MessageBox.OK(file + line.ToString());

            var se = new StoppedEvent(0, "error");
            toVSCode.SendMessage(se);
        }
    }
}
