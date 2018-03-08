module Consensus.Transaction

open TxSkeleton
open Types
open Crypto
open Serialization

let hash =
    serializeTransaction WithoutWitness >> Hash.compute

let witnessHash =
    //TODO: only serialize witness
    serializeTransaction Full >> Hash.compute

let addWitnesses witnesses tx =
    { tx with witnesses = witnesses @ tx.witnesses }

let sign keyPairs tx =
    let txHash = hash tx

    let pkWitnesses =
        List.map (
            fun ((secretKey, publicKey)) -> PKWitness (PublicKey.serialize publicKey, Crypto.sign secretKey txHash)
        ) keyPairs

    //// TODO: Should we also use sighash and not sign entire transaction?
    addWitnesses pkWitnesses tx

let fromTxSkeleton tx =
    {
        inputs = List.map (function
            | TxSkeleton.Input.PointedOutput (outpoint, _) -> Outpoint outpoint
            | TxSkeleton.Input.Mint spend -> Mint spend) tx.pInputs
        outputs = tx.outputs
        witnesses = []
        contract = None
    }

let isOutputSpendable output =
    match output.lock with
    | PK _
    | Coinbase _
    | Contract _ -> true
    | Fee
    | Destroy
    | ActivationSacrifice -> false

// Temporary stuff until we will have blocks
let rootPKHash = Hash.compute [| 3uy; 235uy; 227uy; 69uy; 160uy; 193uy; 130uy; 94uy; 110uy; 75uy; 201uy;
                                 131uy; 186uy; 13uy; 173uy; 220uy; 244uy; 192uy; 5uy; 17uy; 204uy; 211uy;
                                 80uy; 60uy; 34uy; 149uy; 101uy; 37uy; 19uy; 1uy; 22uy; 53uy; 147uy|]

// Temporary transaction until we will have blocks and test genesis block
let rootTx=
    {
        inputs=[];
        outputs=[{lock = PK rootPKHash; spend= {asset = Constants.Zen;amount=100000000UL}}];
        witnesses=[]
        contract=None
    }

let rootTxHash = hash rootTx
