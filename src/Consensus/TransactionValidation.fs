﻿module Consensus.TransactionValidation

open TxSkeleton
open Consensus.Types
open Consensus.UtxoSet
open Consensus.Crypto

let private (>=>) f1 f2 x = Result.bind f2 (f1 x)

type ValidationError =
    | Orphan
    | DoubleSpend
    | General of string

let private GeneralError msg =
    msg |> General |> Error

let private addSpend s m =
    let (+) a b =
        try 
            Some (Operators.Checked.(+) a b) with 
        | :? System.OverflowException -> None
    match Map.tryFind s.asset m with
    | Some (Some v) -> Map.add s.asset (v + s.amount) m
    | Some None -> m
    | None -> Map.add s.asset (0UL + s.amount) m

let private foldSpends = 
    List.map (fun o -> o.spend) 
    >> List.fold (fun map s -> addSpend s map) Map.empty

let private checkSpends m = 
    Map.exists (fun _ v -> Option.isNone v) m |> not

let private checkAmounts (tx, inputs) =
    let cHashes = 
        tx.witnesses
        |> List.choose (function | ContractWitness cw -> Some cw.cHash | _ -> None)

    
    let outputs =
        tx.outputs
        |> List.filter (fun output -> not <| List.contains output.spend.asset cHashes)

    let outputs', inputs' = foldSpends outputs, foldSpends inputs

    if not <| checkSpends outputs' then
        GeneralError "outputs overflow"
    else if not <| checkSpends inputs' then
        GeneralError "inputs overflow"
    else if outputs' <> inputs' then
        GeneralError "invalid amounts"
    else
        Ok (tx,inputs)

let private checkWitnesses getWallet acs contractWallets (Hash.Hash txHash, tx, inputs) =
    let checkPKWitness tx pInputs serializedPublicKey signature =
        match pInputs with
        | [] -> GeneralError "missing PK witness input" 
        | (_, {lock=PK pkHash}) :: tail -> 
            match PublicKey.deserialize serializedPublicKey with
            | Some publicKey ->
                if PublicKey.hashSerialized serializedPublicKey = pkHash then
                    match verify publicKey signature txHash with
                    | Valid -> Ok (tx, tail)
                    | _ -> GeneralError "invalid PK witness signature"
                else GeneralError "PK witness mismatch"
            | _ -> GeneralError "invalid PK witness"
        | _ -> GeneralError "unexpected PK witness lock type"

    let checkContractWitness tx acs cw pInputs =
        let checkIssuedAndDestroyed txSkeleton cw =
            txSkeleton.pInputs.[cw.beginInputs - 1 .. cw.beginInputs + cw.inputsLength - 1]
            |> List.forall (fun (outpoint, {spend=spend}) ->
                not <| TxSkeleton.isSkeletonOutpoint outpoint || spend.asset = cw.cHash) 
            &&
            txSkeleton.outputs.[cw.beginOutputs - 1 .. cw.beginOutputs + cw.outputsLength - 1]
            |> List.forall (
                function
                | {lock=Destroy; spend=spend} -> spend.asset = cw.cHash
                | _ -> true)

        match ActiveContractSet.tryFind cw.cHash acs with
        | Some contract ->
            let contractWallet = (ContractWallets.get getWallet cw.cHash contractWallets)
            match Contract.run contract "" contractWallet tx with 
            | Ok tx' ->
                if checkIssuedAndDestroyed tx' cw then
                    if List.length tx'.pInputs - List.length tx.pInputs = cw.inputsLength && 
                       List.length tx'.outputs - List.length tx.outputs = cw.outputsLength then
                        let rec popContractsLocksOf cHash tx pInputs =
                            match pInputs with
                            | [] -> [] 
                            | (input, output) :: tail ->
                                match output.lock with 
                                | Contract cHash' when cHash' = cHash ->
                                    popContractsLocksOf cHash' tx tail
                                | _ -> (input, output) :: tail
                        Ok (tx', popContractsLocksOf cw.cHash tx' pInputs)
                    else GeneralError "input/output length mismatch"
                else GeneralError "illegal creation/destruction of tokens"
            | Error err -> GeneralError ("contract witness validation error:" + err)
        | None -> GeneralError "contract is not active"

    let witnessesFolder state witness =
        state 
        |> Result.bind (fun (tx', pInputs) ->
            match witness with
            | PKWitness (serializedPublicKey, signature) ->
                checkPKWitness tx' pInputs serializedPublicKey signature
            | ContractWitness cw -> 
                checkContractWitness tx' acs cw pInputs
        )

    let applyMaskIfContract pTx =
        match List.tryPick
            (function
            | ContractWitness cw -> Some cw
            | _ -> None) tx.witnesses with
        | Some cw -> 
            TxSkeleton.applyMask pTx cw
        | _ -> 
            Ok pTx

    TxSkeleton.fromTransaction tx inputs 
    |> Result.bind applyMaskIfContract
    |> Result.mapError (fun err -> General err)
    |> Result.bind (fun tx' -> 
        List.fold witnessesFolder (Ok (tx', tx'.pInputs)) tx.witnesses)
    |> Result.bind (
        fun (tx', pInputs) -> 
            if not <| List.isEmpty pInputs then
                GeneralError "missing witness(s)"
            else if not <| TxSkeleton.isSkeletonOf tx' tx then
                GeneralError "contract validation failed"
            else 
                Ok (tx, inputs)
    )

let private checkInputsNotEmpty tx = 
    if List.isEmpty tx.inputs then GeneralError "inputs empty"
    else Ok tx

let private checkOutputsNotEmpty tx =
    if List.isEmpty tx.outputs then
        GeneralError "outputs empty"
    else if List.exists (fun output -> output.spend.amount = 0UL) tx.outputs then
        GeneralError "outputs invalid"
    else
        Ok tx

let private checkOutputsOverflow tx =
    if tx.outputs 
          |> foldSpends
          |> checkSpends then Ok tx
    else GeneralError "outputs overflow"

let private checkDuplicateInputs tx =
    let (==) a b = List.length a = List.length b
    if List.distinct tx.inputs == tx.inputs then Ok tx
    else GeneralError "inputs duplicated"

let private checkInputsStructure tx =
    if tx.inputs |> List.exists (fun input -> not (Hash.isValid input.txHash)) then
        GeneralError "inputs structurally invalid"
    else
        Ok tx

let private checkOrphan getUTXO set txHash tx =
    match getUtxos getUTXO tx.inputs set with
    | Some utxos -> 
        Ok (txHash, tx, utxos)
    | None -> 
        match UtxoSet.isSomeSpent getUTXO tx.inputs set with
        | true -> DoubleSpend
        | false -> Orphan
        |> Error

let validateBasic = 
    checkInputsNotEmpty
    >=> checkOutputsNotEmpty
    >=> checkOutputsOverflow
    >=> checkDuplicateInputs
    >=> checkInputsStructure

let validateInputs getUTXO getWallet acs set contractWallets txHash =
    checkOrphan getUTXO set txHash
    >=> checkWitnesses getWallet acs contractWallets
    >=> checkAmounts