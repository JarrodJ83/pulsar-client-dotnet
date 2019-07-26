﻿namespace Pulsar.Client.Internal

open Pulsar.Client.Common
open System.Collections.Generic
open FSharp.Control.Tasks.V2.ContextInsensitive
open Microsoft.Extensions.Logging
open System.Threading.Tasks
open pulsar.proto
open System
open System.IO.Pipelines
open FSharp.UMX
open System.Buffers
open System.IO
open ProtoBuf
open CRC32
open Pulsar.Client.Api

type CnxOperation =
    | AddProducer of ProducerId * MailboxProcessor<ProducerMessage>
    | AddConsumer of ConsumerId * MailboxProcessor<ConsumerMessage>
    | RemoveConsumer of ConsumerId
    | RemoveProducer of ProducerId
    | ChannelInactive

type PulsarCommand =
    | XCommandConnected of CommandConnected
    | XCommandPartitionedTopicMetadataResponse of CommandPartitionedTopicMetadataResponse
    | XCommandSendReceipt of CommandSendReceipt
    | XCommandMessage of (CommandMessage * MessageMetadata * byte[] )
    | XCommandPing of CommandPing
    | XCommandLookupTopicResponse of CommandLookupTopicResponse
    | XCommandProducerSuccess of CommandProducerSuccess
    | XCommandSuccess of CommandSuccess
    | XCommandSendError of CommandSendError
    | XCommandGetTopicsOfNamespaceResponse of CommandGetTopicsOfNamespaceResponse
    | XCommandCloseProducer of CommandCloseProducer
    | XCommandCloseConsumer of CommandCloseConsumer
    | XCommandReachedEndOfTopic of CommandReachedEndOfTopic
    | XCommandError of CommandError

type CommandParseError =
    | IncompleteCommand
    | UnknownCommandType of BaseCommand.Type

type SocketMessage =
    | SocketMessageWithReply of Payload * AsyncReplyChannel<unit>
    | SocketMessageWithoutReply of Payload
    | SocketRequestMessageWithReply of RequestId * Payload * AsyncReplyChannel<Task<PulsarTypes>>
    | Stop

type ClientCnx (broker: Broker,
                connection: Connection,
                initialConnectionTsc: TaskCompletionSource<ClientCnx>,
                unregisterClientCnx: Broker -> unit) as this =

    let consumers = Dictionary<ConsumerId, MailboxProcessor<ConsumerMessage>>()
    let producers = Dictionary<ProducerId, MailboxProcessor<ProducerMessage>>()
    let requests = Dictionary<RequestId, TaskCompletionSource<PulsarTypes>>()

    let operationsMb = MailboxProcessor<CnxOperation>.Start(fun inbox ->
        let rec loop () =
            async {
                match! inbox.Receive() with
                | AddProducer (producerId, mb) ->
                    producers.Add(producerId, mb)
                    return! loop()
                | AddConsumer (consumerId, mb) ->
                    consumers.Add(consumerId, mb)
                    return! loop()
                | RemoveConsumer consumerId ->
                    consumers.Remove(consumerId) |> ignore
                    return! loop()
                | RemoveProducer producerId ->
                    producers.Remove(producerId) |> ignore
                    return! loop()
                | ChannelInactive ->
                    unregisterClientCnx(broker)
                    this.SendMb.Post(Stop)
                    consumers |> Seq.iter(fun kv ->
                        kv.Value.Post(ConsumerMessage.ConnectionClosed))
                    producers |> Seq.iter(fun kv ->
                        kv.Value.Post(ProducerMessage.ConnectionClosed))
            }
        loop ()
    )

    let sendSerializedPayload (serializedPayload: Payload ) =
        task {
            let (conn, streamWriter) = connection
            do! streamWriter |> serializedPayload

            let connected = conn.Socket.Connected
            if (not connected) then
                Log.Logger.LogWarning("Socket was disconnected on writing {0}", broker.LogicalAddress)
                operationsMb.Post(ChannelInactive)
            return connected
        }

    let sendMb = MailboxProcessor<SocketMessage>.Start(fun inbox ->
        let rec loop () =
            async {
                match! inbox.Receive() with
                | SocketMessageWithReply (payload, replyChannel) ->
                    let! connected = sendSerializedPayload payload |> Async.AwaitTask
                    replyChannel.Reply()
                    if connected then
                        return! loop ()
                | SocketMessageWithoutReply payload ->
                    let! connected = sendSerializedPayload payload |> Async.AwaitTask
                    if connected then
                        return! loop ()
                | SocketRequestMessageWithReply (reqId, payload, replyChannel) ->
                    let! connected = sendSerializedPayload payload |> Async.AwaitTask
                    let tsc = TaskCompletionSource()
                    requests.Add(reqId, tsc)
                    replyChannel.Reply(tsc.Task)
                    if connected then
                        return! loop ()
                    else
                        tsc.SetException(Exception("Disconnected"))
                | Stop -> ()
            }
        loop ()
    )

    let tryParse (buffer: ReadOnlySequence<byte>) readerId =
        let array = buffer.ToArray()
        if (array.Length >= 8)
        then
            use stream =  new MemoryStream(array)
            use reader = new BinaryReader(stream)

            let totalength = reader.ReadInt32() |> int32FromBigEndian
            let frameLength = totalength + 4

            if (array.Length >= frameLength)
            then
                let command = Serializer.DeserializeWithLengthPrefix<BaseCommand>(stream, PrefixStyle.Fixed32BigEndian)
                Log.Logger.LogDebug("[{0}] Got message of type {1}", readerId, command.``type``)
                let consumed : SequencePosition = int64 frameLength |> buffer.GetPosition
                match command.``type`` with
                | BaseCommand.Type.Connected ->
                    Ok (XCommandConnected command.Connected, consumed)
                | BaseCommand.Type.PartitionedMetadataResponse ->
                    Ok (XCommandPartitionedTopicMetadataResponse command.partitionMetadataResponse, consumed)
                | BaseCommand.Type.SendReceipt ->
                    Ok (XCommandSendReceipt command.SendReceipt, consumed)
                | BaseCommand.Type.Message ->
                    reader.ReadInt16() |> int16FromBigEndian |> invalidArgIf ((<>) MagicNumber) "Invalid magicNumber" |> ignore
                    let messageCheckSum  = reader.ReadInt32() |> int32FromBigEndian
                    let metadataPointer = stream.Position
                    let metatada = Serializer.DeserializeWithLengthPrefix<MessageMetadata>(stream, PrefixStyle.Fixed32BigEndian)
                    let payloadPointer = stream.Position
                    let metadataLength = payloadPointer - metadataPointer |> int
                    let payloadLength = frameLength - (int payloadPointer)
                    let payload = reader.ReadBytes(payloadLength)
                    stream.Seek(metadataPointer, SeekOrigin.Begin) |> ignore
                    CRC32C.Get(0u, stream, metadataLength + payloadLength) |> int32 |> invalidArgIf ((<>) messageCheckSum) "Invalid checksum" |> ignore
                    Ok (XCommandMessage (command.Message, metatada, payload), consumed)
                | BaseCommand.Type.LookupResponse ->
                    Ok (XCommandLookupTopicResponse command.lookupTopicResponse, consumed)
                | BaseCommand.Type.Ping ->
                    Ok (XCommandPing command.Ping, consumed)
                | BaseCommand.Type.ProducerSuccess ->
                    Ok (XCommandProducerSuccess command.ProducerSuccess, consumed)
                | BaseCommand.Type.Success ->
                    Ok (XCommandSuccess command.Success, consumed)
                | BaseCommand.Type.SendError ->
                    Ok (XCommandSendError command.SendError, consumed)
                | BaseCommand.Type.CloseProducer ->
                    Ok (XCommandCloseProducer command.CloseProducer, consumed)
                | BaseCommand.Type.CloseConsumer ->
                    Ok (XCommandCloseConsumer command.CloseConsumer, consumed)
                | BaseCommand.Type.ReachedEndOfTopic ->
                    Ok (XCommandReachedEndOfTopic command.reachedEndOfTopic, consumed)
                | BaseCommand.Type.GetTopicsOfNamespaceResponse ->
                    Ok (XCommandGetTopicsOfNamespaceResponse command.getTopicsOfNamespaceResponse, consumed)
                | BaseCommand.Type.Error ->
                    Ok (XCommandError command.Error, consumed)
                | unknownType ->
                    Result.Error (UnknownCommandType unknownType)
            else Result.Error IncompleteCommand
        else Result.Error IncompleteCommand

    let handleRespone requestId result =
        let tsc = requests.[requestId]
        tsc.SetResult result
        requests.Remove requestId |> ignore

    let readSocket () =
        task {
            let readerId = Generators.getNextSocketReaderId()
            Log.Logger.LogDebug("[{0}] Started read socket for {1}", readerId, broker)
            let (conn, _) = connection
            let mutable continueLooping = true
            let reader = conn.Input
            while continueLooping do
                let! result = reader.ReadAsync()
                let buffer = result.Buffer
                if result.IsCompleted
                then
                    if
                        initialConnectionTsc.TrySetException(Exception("Unable to initiate connection"))
                    then
                        Log.Logger.LogWarning("[{0}] New connection to {1} was aborted", readerId, broker)
                    Log.Logger.LogWarning("[{0}] Socket was disconnected while reading {1}", readerId, broker)
                    operationsMb.Post(ChannelInactive)
                    continueLooping <- false
                else
                    match tryParse buffer readerId with
                    | Result.Ok (xcmd, consumed) ->
                        match xcmd with
                        | XCommandConnected _ ->
                            //TODO check server protocol version
                            initialConnectionTsc.SetResult(this)
                        | XCommandPartitionedTopicMetadataResponse cmd ->
                            let result =
                                if (cmd.ShouldSerializeError())
                                then
                                    Log.Logger.LogError("Error: {0}. Message: {1}", cmd.Error, cmd.Message)
                                    Error
                                else
                                    PartitionedTopicMetadata { Partitions = cmd.Partitions }
                            handleRespone %cmd.RequestId result
                        | XCommandSendReceipt cmd ->
                            let producerMb = producers.[%cmd.ProducerId]
                            producerMb.Post(SendReceipt cmd)
                        | XCommandSendError cmd ->
                            let producerMb = producers.[%cmd.ProducerId]
                            producerMb.Post(SendError cmd)
                        | XCommandPing _ ->
                            Commands.newPong() |> SocketMessageWithoutReply |> sendMb.Post
                        | XCommandMessage (cmd, _, payload) ->
                            let consumerMb = consumers.[%cmd.ConsumerId]
                            consumerMb.Post(MessageRecieved { MessageId = MessageId.FromMessageIdData(cmd.MessageId); Payload = payload })
                        | XCommandLookupTopicResponse cmd ->
                            let result =
                                if (cmd.ShouldSerializeError())
                                then
                                    Log.Logger.LogError("Error: {0}. Message: {1}", cmd.Error, cmd.Message)
                                    Error
                                else
                                    LookupTopicResult { BrokerServiceUrl = cmd.brokerServiceUrl; Proxy = cmd.ProxyThroughServiceUrl }
                            handleRespone %cmd.RequestId result
                        | XCommandProducerSuccess cmd ->
                            let result = ProducerSuccess { GeneratedProducerName = cmd.ProducerName }
                            handleRespone %cmd.RequestId result
                        | XCommandSuccess cmd ->
                            handleRespone %cmd.RequestId Empty
                        | XCommandCloseProducer cmd ->
                            let producerMb = producers.[%cmd.ProducerId]
                            producers.Remove(%cmd.ProducerId) |> ignore
                            producerMb.Post ProducerMessage.ConnectionClosed
                        | XCommandCloseConsumer cmd ->
                            let consumerMb = consumers.[%cmd.ConsumerId]
                            consumers.Remove(%cmd.ConsumerId) |> ignore
                            consumerMb.Post ConnectionClosed
                        | XCommandReachedEndOfTopic cmd ->
                            let consumerMb = consumers.[%cmd.ConsumerId]
                            consumerMb.Post ReachedEndOfTheTopic
                        | XCommandGetTopicsOfNamespaceResponse cmd ->
                            let result = TopicsOfNamespace { Topics = List.ofSeq cmd.Topics }
                            handleRespone %cmd.RequestId result
                        | XCommandError cmd ->
                            Log.Logger.LogError("Error: {0}. Message: {1}", cmd.Error, cmd.Message)
                            let result = Error
                            handleRespone %cmd.RequestId result

                        reader.AdvanceTo consumed

                    | Result.Error IncompleteCommand ->
                        reader.AdvanceTo(buffer.Start, buffer.End)
                    | Result.Error (UnknownCommandType unknownType) ->
                        failwithf "Unknown command type %A" unknownType
            Log.Logger.LogDebug("[{0}] Finished read socket for {1}", readerId, broker)
        }

    do Task.Run(fun () -> readSocket().Wait()) |> ignore

    member private __.SendMb with get(): MailboxProcessor<SocketMessage> = sendMb

    member __.Send payload =
        sendMb.PostAndAsyncReply(fun replyChannel -> SocketMessageWithReply(payload, replyChannel))

    member __.SendAndWaitForReply reqId payload =
        task {
            let! task = sendMb.PostAndAsyncReply(fun replyChannel -> SocketRequestMessageWithReply(reqId, payload, replyChannel))
            return! task
        }

    member __.RemoveConsumer (consumerId: ConsumerId) =
        operationsMb.Post(RemoveConsumer(consumerId))

    member __.RemoveProducer (consumerId: ProducerId) =
        operationsMb.Post(RemoveProducer(consumerId))

    member __.AddProducer (producerId: ProducerId) (producerMb: MailboxProcessor<ProducerMessage>) =
        operationsMb.Post(AddProducer (producerId, producerMb))

    member __.AddConsumer (consumerId: ConsumerId) (consumerMb: MailboxProcessor<ConsumerMessage>) =
        operationsMb.Post(AddConsumer (consumerId, consumerMb))

    member __.Close() =
        let (conn, writeStream) = connection
        conn.Dispose()
        writeStream.Dispose()
