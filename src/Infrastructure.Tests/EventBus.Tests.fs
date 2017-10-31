module EventBus.Tests

open Xunit
open FsUnit.Xunit
open FsNetMQ

type Messages = 
    | Hello
    | World of string

[<Fact>]
let ``send and recv message`` () =                  
    use brokerActor = Actor.create (fun shim ->
        use poller = Poller.create ()
        use broker = EventBus.Broker.create poller "test"
    
        use observer = 
            Poller.addSocket poller shim |>
            Observable.subscribe (fun _ ->
                Frame.recv shim |> ignore
                Poller.stop poller
                )
    
        Actor.signal shim
        Poller.run poller 
    )      
    
    use pubActor = Actor.create (fun shim ->
        use poller = Poller.create ()
        use agent = EventBus.Agent.create<Messages> poller "test"
    
        Actor.signal shim
        
        System.Threading.Thread.Sleep 10
        Agent.publish agent (World "Hello")
        
        // Wait for signal to exit
        Frame.recv shim |> ignore                               
    )                                
        
    use poller = Poller.create ()
    use agent = EventBus.Agent.create<Messages> poller "test"
    use observer = 
        EventBus.Agent.observable agent
        |> Observable.subscribe (fun msg -> 
            Poller.stop poller            
            msg |> should equal (World "Hello"))
                
    Poller.run poller                
             
        