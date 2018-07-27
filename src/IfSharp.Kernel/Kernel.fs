namespace IfSharp.Kernel

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Security.Cryptography

open QSEvaluator

open Newtonsoft.Json
open NetMQ
open NetMQ.Sockets

/// A function that by it's side effect sends the received dict as a comm_message
type SendCommMessage = Dictionary<string,obj> -> unit

type CommOpenCallback = SendCommMessage -> CommOpen -> unit
type CommMessageCallback = SendCommMessage -> CommMessage -> unit
type CommCloseCallback = CommTearDown  -> unit

type CommId = string
type CommTargetName = string

/// The set of callbacks which define comm registration at the kernell side
type CommCallbacks = {
    /// called upon comm creation
    onOpen : CommOpenCallback
    /// called to handle every received message while the come is opened
    onMessage : CommMessageCallback
    /// called upon comm close
    onClose: CommCloseCallback
    }


type IfSharpKernel(connectionInformation : ConnectionInformation) = 
    // heartbeat
    let hbSocket = new RouterSocket()
    let hbSocketURL = String.Format("{0}://{1}:{2}", connectionInformation.transport, connectionInformation.ip, connectionInformation.hb_port) 
    do hbSocket.Bind(hbSocketURL)
        
    // control
    let controlSocket = new RouterSocket()
    let controlSocketURL = String.Format("{0}://{1}:{2}", connectionInformation.transport, connectionInformation.ip, connectionInformation.control_port)
    do controlSocket.Bind(controlSocketURL)

    // stdin
    let stdinSocket = new RouterSocket()
    let stdinSocketURL = String.Format("{0}://{1}:{2}", connectionInformation.transport, connectionInformation.ip, connectionInformation.stdin_port)
    do stdinSocket.Bind(stdinSocketURL)

    // iopub
    let ioSocket = new PublisherSocket()
    let ioSocketURL = String.Format("{0}://{1}:{2}", connectionInformation.transport, connectionInformation.ip, connectionInformation.iopub_port)
    do ioSocket.Bind(ioSocketURL)

    // shell
    let shellSocket = new RouterSocket()
    let shellSocketURL =String.Format("{0}://{1}:{2}", connectionInformation.transport, connectionInformation.ip, connectionInformation.shell_port)
    do shellSocket.Bind(shellSocketURL)

    let mutable executionCount = 0
    let mutable lastMessage : Option<KernelMessage> = None

    /// Registered comm difinitions (can be activated from Frontend side by comm_open message containing registered comm_target name)
    let mutable registeredComms : Map<CommTargetName,CommCallbacks> = Map.empty;
    /// Comms that are in the open state
    let mutable activeComms : Map<CommId,CommTargetName> = Map.empty;

    /// Splits the message up into lines and writes the lines to shell.log
    let logMessage (msg : string) =
        let fileName = "shell.log"
        let messages = 
            msg.Split('\r', '\n')
            |> Seq.filter (fun x -> x <> "")
            |> Seq.map (fun x -> String.Format("{0:yyyy-MM-dd HH:mm:ss} - {1}", DateTime.Now, x))
            |> Seq.toArray
        try
            File.AppendAllLines(fileName, messages)
        with _ -> ()

    /// Logs the exception to the specified file name
    let handleException (ex : exn) = 
        let message = ex.CompleteStackTrace()
        logMessage message

    /// Decodes byte array into a string using UTF8
    let decode (bytes) =
        Encoding.UTF8.GetString(bytes)

    /// Encodes a string into a byte array using UTF8
    let encode (str : string) =
        Encoding.UTF8.GetBytes(str)

    /// Serializes any object into JSON
    let serialize (obj) =
        let ser = JsonSerializer()
        let sw = new StringWriter()
        ser.Serialize(sw, obj)
        sw.ToString()

    /// Sign a set of strings.
    let hmac = new HMACSHA256(Encoding.UTF8.GetBytes(connectionInformation.key))
    let sign (parts:string list) : string =
        if connectionInformation.key = "" then "" else
          ignore (hmac.Initialize())
          List.iter (fun (s:string) -> let bytes = Encoding.UTF8.GetBytes(s) in ignore(hmac.TransformBlock(bytes, 0, bytes.Length, null, 0))) parts
          ignore (hmac.TransformFinalBlock(Array.zeroCreate 0, 0, 0))
          BitConverter.ToString(hmac.Hash).Replace("-", "").ToLower()

    let recvAll (socket: NetMQSocket) = socket.ReceiveMultipartBytes()
    
    /// Constructs an 'envelope' from the specified socket
    let recvMessage (socket: NetMQSocket) = 
        
        // receive all parts of the message
        let message = (recvAll (socket)) |> Array.ofSeq
        let asStrings = message |> Array.map decode

        // find the delimiter between IDS and MSG
        let idx = Array.IndexOf(asStrings, "<IDS|MSG>")

        let idents = message.[0..idx - 1]
        let messageList = asStrings.[idx + 1..message.Length - 1]

        // detect a malformed message
        if messageList.Length < 4 then failwith ("Malformed message")

        // assemble the 'envelope'
        let hmac             = messageList.[0]
        let headerJson       = messageList.[1]
        let parentHeaderJson = messageList.[2]
        let metadata         = messageList.[3]
        let contentJson      = messageList.[4]
        
        let header           = JsonConvert.DeserializeObject<Header>(headerJson)
        let parentHeader     = JsonConvert.DeserializeObject<Header>(parentHeaderJson)
        let content          = ShellMessages.Deserialize (header.msg_type) (contentJson)

        let calculated_signature = sign [headerJson; parentHeaderJson; metadata; contentJson]
        if calculated_signature <> hmac then failwith("Wrong message signature")

        lastMessage <- Some
            {
                Identifiers = idents |> Seq.toList;
                HmacSignature = hmac;
                Header = header;
                ParentHeader = parentHeader;
                Metadata = metadata;
                Content = content;
            }

        lastMessage.Value

    /// Convenience method for creating a header
    let createHeader (messageType) (sourceEnvelope) =
        {
            msg_type = messageType;
            msg_id = Guid.NewGuid().ToString();
            session = sourceEnvelope.Header.session;
            username = sourceEnvelope.Header.username;
        }

    /// Convenience method for sending a message
    let sendMessage (socket: NetMQSocket) (envelope) (messageType) (content) =

        let header = createHeader messageType envelope
        let msg = NetMQMessage()

        for ident in envelope.Identifiers do
            msg.Append(ident)

        let header = serialize header
        let parent_header = serialize envelope.Header
        let meta = "{}"
        let content = serialize content
        let signature = sign [header; parent_header; meta; content]

        msg.Append(encode "<IDS|MSG>")
        msg.Append(encode signature)
        msg.Append(encode header)
        msg.Append(encode parent_header)
        msg.Append(encode "{}")
        msg.Append(encode content)
        socket.SendMultipartMessage(msg)

        
    /// Convenience method for sending the state of the kernel
    let sendState (envelope) (state) =
        sendMessage ioSocket envelope "status" { execution_state = state } 

    /// Convenience method for sending the state of 'busy' to the kernel
    let sendStateBusy (envelope) =
        sendState envelope "busy"

    /// Convenience method for sending the state of 'idle' to the kernel
    let sendStateIdle (envelope) =
        sendState envelope "idle"

    /// Handles a 'kernel_info_request' message
    let kernelInfoRequest(msg : KernelMessage) (content : KernelRequest) = 
        let content = 
            {
                protocol_version = "4.0.0";
                implementation = "ifsharp";
                implementation_version = "4.0.0";
                banner = "";
                help_links = [||];
                language = "fsharp";
                language_info =
                {
                    name = "fsharp";
                    version = "4.3.1.0";
                    mimetype = "text/x-fsharp";
                    file_extension = ".fs";
                    pygments_lexer = "";
                    codemirror_mode = "";
                    nbconvert_exporter = "";
                };
            }

        sendStateBusy msg
        sendMessage shellSocket msg "kernel_info_reply" content

    /// Sends display data information immediately
    let sendDisplayData (contentType) (displayItem) (messageType) =        
        if lastMessage.IsSome then

            let d = Dictionary<string,obj>()
            d.Add(contentType, displayItem)

            let reply = { execution_count = executionCount; data = d; metadata = Dictionary<string,obj>() }
            sendMessage ioSocket lastMessage.Value messageType reply


    let mutable evaluator : QsEvaluator Option = None

    /// Handles an 'execute_request' message
    let executeRequest(msg : KernelMessage) (content : ExecuteRequest) = 
        let e =
            match evaluator with
            | Some v -> v
            | None ->
                evaluator <- Some <| new QsEvaluator()
                evaluator.Value        

        // only increment if we are not silent
        if not content.silent then executionCount <- executionCount + 1
        
        // send busy
        sendStateBusy msg
        sendMessage ioSocket msg "pyin" { code = content.code; execution_count = executionCount  }

        DateTime.Now |> printfn "Time recieved code: %A"

        let result = e.EvaluateStatement(content.code)

        DateTime.Now |> printfn "Time evaluated code: %A"

        sendMessage ioSocket msg "stream" { name = "stdout"; data = result.Output; }
        sendMessage ioSocket msg "stream" { name = "stderr"; data = result.ErrOutput; }

        let executeReply =
            {
                status = "ok";
                execution_count = executionCount;
                payload = [];
                user_variables = Dictionary<string,obj>();
                user_expressions = Dictionary<string,obj>()
            }

        sendMessage shellSocket msg "execute_reply" executeReply

        // we are now idle
        sendStateIdle msg

    // None for Q#
    let completeRequest _ _ = ()
        
    let intellisenseRequest _ _ = ()

    /// Handles a 'connect_request' message
    let connectRequest (msg : KernelMessage) _ = 

        let reply =
            {
                hb_port = connectionInformation.hb_port;
                iopub_port = connectionInformation.iopub_port;
                shell_port = connectionInformation.shell_port;
                stdin_port = connectionInformation.stdin_port; 
            }

        logMessage "connectRequest()"
        sendMessage shellSocket msg "connect_reply" reply

    /// Handles a 'shutdown_request' message
    let shutdownRequest (msg : KernelMessage) (content : ShutdownRequest) =
        match evaluator with
        | Some e -> e.Dispose()
        | None -> ()

        logMessage "shutdown request"
  
        let reply = { restart = content.restart; }

        sendMessage shellSocket msg "shutdown_reply" reply;

        if content.restart then evaluator <- Some <| new QsEvaluator() else exit 0

    /// Handles a 'history_request' message
    let historyRequest (msg : KernelMessage) _ =
        // TODO: actually handle this
        sendMessage shellSocket msg "history_reply" { history = [] }

    let objectInfoRequest _ _ = ()

    let inspectRequest (msg : KernelMessage) _ =
        let reply = { status = "ok"; found = false; data = Dictionary<string,obj>(); metadata = Dictionary<string,obj>() }
        sendMessage shellSocket msg "inspect_reply" reply
        ()

    let sendCommData sourceEnvelope commId (data:Dictionary<string,obj>) =
        let message : CommMessage = {comm_id=commId; data = data}
        sendMessage ioSocket sourceEnvelope "comm_msg" message

    let commOpen (msg : KernelMessage) (content : CommOpen) =
        if String.IsNullOrEmpty(content.target_name) then
            // as defined in protocol
            let reply: CommTearDown = {comm_id = content.comm_id; data = Dictionary<string,obj>();}
            sendMessage ioSocket msg "comm_close" reply
        match Map.tryFind content.target_name registeredComms with
        |   Some callbacks ->
            // executing open callback
            let onOpen = callbacks.onOpen
            let sendOnjectWithComm = sendCommData msg content.comm_id 
            onOpen sendOnjectWithComm content
            // saving comm_id for created instance 
            activeComms <- Map.add content.comm_id content.target_name activeComms
            logMessage (sprintf "comm opened id=%s target_name=%s" content.comm_id content.target_name)
        |   None ->            
            logMessage (sprintf "received comOpen request for the unknown com target_name \"%s\". Please register comm with this target_name first." content.target_name)
            let reply: CommTearDown = {comm_id = content.comm_id; data = Dictionary<string,obj>();}
            sendMessage ioSocket msg "comm_close" reply
    
    let commMessage (msg : KernelMessage) (content : CommMessage) =
        match Map.tryFind content.comm_id activeComms with
        |   Some comm_target ->
            // finding corresponding callback
            let callbacks = Map.find comm_target registeredComms
            // and executing it
            let onMessage = callbacks.onMessage
            let sendOnjectWithComm = sendCommData msg content.comm_id 
            onMessage sendOnjectWithComm content
            logMessage (sprintf "comm message handled id=%s target_name=%s" content.comm_id comm_target)
        |   None -> logMessage (sprintf "Got comm message (comm_id=%s), but there is nor opened comms with such comm_id. Ignoring" content.comm_id)

    let commClose (msg : KernelMessage) (content : CommTearDown) =        
        match Map.tryFind content.comm_id activeComms with
        |   Some target_name ->
            // executing close callback
            let callbacks = Map.find target_name registeredComms
            callbacks.onClose content
            // removing comm from opened comms
            activeComms <- Map.remove content.comm_id activeComms
            logMessage (sprintf "comm closed id=%s target_name=%s" content.comm_id target_name)
        |   None -> logMessage (sprintf "Got comm close request (comm_id=%s), but there is nor opened comms with such comm_id" content.comm_id)
    
    let commInfoRequest (msg : KernelMessage) (content : CommInfoRequest) =
        // returning all open comms
        let pairToDict pair =
            let comm_id,target_name = pair
            let dict = new Dictionary<string,string>();
            dict.Add("target_name",target_name)
            comm_id,dict        
        let openedCommsDict  = Dictionary<string,Dictionary<string,string>>()
        activeComms |> Map.toSeq |> Seq.map pairToDict |> Seq.iter (fun entry -> let key,value = entry in openedCommsDict.Add(key,value))
        let reply = { comms = openedCommsDict}
        sendMessage shellSocket msg "comm_info_reply" reply
        logMessage (sprintf "Reporting %d opened comms" openedCommsDict.Count)


    /// Loops forever receiving messages from the client and processing them
    let doShell() =

        while true do
            let msg = recvMessage (shellSocket)

            try
                match msg.Content with
                | KernelRequest(r)       -> kernelInfoRequest msg r
                | ExecuteRequest(r)      -> executeRequest msg r
                | CompleteRequest(r)     -> completeRequest msg r
                | IntellisenseRequest(r) -> intellisenseRequest msg r
                | ConnectRequest(r)      -> connectRequest msg r
                | ShutdownRequest(r)     -> shutdownRequest msg r
                | HistoryRequest(r)      -> historyRequest msg r
                | ObjectInfoRequest(r)   -> objectInfoRequest msg r
                | InspectRequest(r)      -> inspectRequest msg r
                | CommOpen(r)            -> commOpen msg r
                | CommMessage(r)         -> commMessage msg r
                | CommTearDown(r)        -> commClose msg r
                | CommInfoRequest(r)     -> commInfoRequest msg r
                | _                      -> logMessage (String.Format("Unknown content type on shell. msg_type is `{0}`", msg.Header.msg_type))
            with 
            | ex -> handleException ex
   
    let doControl() =
        while true do
            let msg = recvMessage (controlSocket)
            try
                match msg.Content with
                | ShutdownRequest(r)     -> shutdownRequest msg r
                | _                      -> logMessage (String.Format("Unexpected content type on control. msg_type is `{0}`", msg.Header.msg_type))
            with 
            | ex -> handleException ex

    /// Adds display data to the list of display data to send to the client
    member __.SendDisplayData (contentType, displayItem) =
        sendDisplayData contentType displayItem "display_data"

    /// Starts the kernel asynchronously
    member __.StartAsync() = 
        
        //Async.Start (async { doHeartbeat() } )
        Async.Start (async { doShell() } )
        Async.Start (async { doControl() } )