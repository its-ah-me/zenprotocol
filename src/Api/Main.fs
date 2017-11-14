﻿module Api.Main

open Api
open FsNetMQ
open Infrastructure
open Messaging.Services.Network
open Messaging.Events
open FSharp.Control

type State = Server.T

let eventHandler event (state:State) = state

let main busName bind =
    Actor.create busName "Api" (fun poller sbObservable ebObservable ->            
        use server = Server.create poller busName bind
                
        let ebObservable = 
            ebObservable
            |> Observable.map eventHandler
            
        let httpObservable = 
            Server.observable server                        
            
                     
        ebObservable
        |> Observable.merge httpObservable             
        |> Observable.scan (fun state handler -> handler state) server        
    )