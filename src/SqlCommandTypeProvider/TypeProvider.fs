﻿namespace FSharp.Data.SqlClient

open System
open System.Data
open System.Data.SqlClient
open System.Reflection
open System.Collections.Generic
open System.Threading

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection

open Samples.FSharp.ProvidedTypes

type ResultType =
    | Tuples = 0
    | Records = 1
    | DataTable = 3

[<TypeProvider>]
type public SqlCommandTypeProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let mutable watcher = null : IDisposable

    let nameSpace = this.GetType().Namespace
    let assembly = Assembly.GetExecutingAssembly()
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "SqlCommand", Some typeof<obj>, HideObjectMethods = true)

    do 
        providerType.DefineStaticParameters(
            parameters = [ 
                ProvidedStaticParameter("CommandText", typeof<string>) 
                ProvidedStaticParameter("ConnectionString", typeof<string>, "") 
                ProvidedStaticParameter("ConnectionStringName", typeof<string>, "") 
                ProvidedStaticParameter("CommandType", typeof<CommandType>, CommandType.Text) 
                ProvidedStaticParameter("ResultType", typeof<ResultType>, ResultType.Tuples) 
                ProvidedStaticParameter("SingleRow", typeof<bool>, false) 
                ProvidedStaticParameter("ConfigFile", typeof<string>, "") 
                ProvidedStaticParameter("DataDirectory", typeof<string>, "") 
            ],             
            instantiationFunction = this.CreateType
        )
        this.AddNamespace(nameSpace, [ providerType ])
    
    interface IDisposable with 
        member this.Dispose() = 
           if watcher <> null
           then try watcher.Dispose() with _ -> ()

    member internal this.CreateType typeName parameters = 
        let commandText : string = unbox parameters.[0] 
        let connectionStringProvided : string = unbox parameters.[1] 
        let connectionStringName : string = unbox parameters.[2] 
        let commandType : CommandType = unbox parameters.[3] 
        let resultType : ResultType = unbox parameters.[4] 
        let singleRow : bool = unbox parameters.[5] 
        let configFile : string = unbox parameters.[6] 
        let dataDirectory : string = unbox parameters.[7] 

        let resolutionFolder = config.ResolutionFolder
        let commandText, watcher' = 
            Configuration.ParseTextAtDesignTime(commandText, resolutionFolder, fun() -> this.Invalidate())
        watcher' |> Option.iter (fun x -> watcher <- x)
        let designTimeConnectionString =  Configuration.GetConnectionString(resolutionFolder, connectionStringProvided, connectionStringName, configFile)
        
        use connection = new SqlConnection(designTimeConnectionString)
        connection.Open()
        connection.CheckVersion()
        connection.LoadDataTypesMap()

        let providedCommandType = ProvidedTypeDefinition(assembly, nameSpace, typeName, baseType = Some typeof<obj>, HideObjectMethods = true)

        providedCommandType.AddMembersDelayed <| fun () -> 
            [
                let parameters = this.ExtractParameters(designTimeConnectionString, commandText, commandType)

                yield! this.AddPropertiesForParameters(parameters) 
                let ctor = ProvidedConstructor( [ ProvidedParameter("connectionString", typeof<string>, optionalValue = Unchecked.defaultof<string>) ])
                ctor.InvokeCode <- fun args -> 
                    <@@ 
                        let runTimeConnectionString = 
                            if String.IsNullOrEmpty(%%args.[0])
                            then
                                Configuration.GetConnectionString (resolutionFolder, connectionStringProvided, connectionStringName, configFile)
                            else 
                                %%args.[0]
                        do
                            if dataDirectory <> ""
                            then AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectory)

                        let this = new SqlCommand(commandText, new SqlConnection(runTimeConnectionString)) 
                        this.CommandType <- commandType
                        let xs : SqlParameter[] = %%Expr.NewArray(typeof<SqlParameter>, parameters |> List.map QuotationsFactory.ToSqlParam)
                        this.Parameters.AddRange xs
                        this
                    @@>

                yield ctor :> MemberInfo    
            ]
        
        
        let outputColumns = this.GetOutputColumns(connection, commandText)
        
        this.AddExecuteMethod(outputColumns, providedCommandType, resultType, singleRow, commandText) 
        
        let getSqlCommandCopy = ProvidedMethod("AsSqlCommand", [], typeof<SqlCommand>)
        getSqlCommandCopy.InvokeCode <- fun args ->
            <@@
                let self : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                let clone = self.Clone()
                clone.Connection <- new SqlConnection(self.Connection.ConnectionString)
                clone
            @@>
        providedCommandType.AddMember getSqlCommandCopy          

        providedCommandType

    member internal this.GetOutputColumns(connection, commandText) = [
        use cmd = new SqlCommand("sys.sp_describe_first_result_set", connection, CommandType = CommandType.StoredProcedure)
        cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
        use reader = cmd.ExecuteReader()

        while reader.Read() do

            let name = string reader.["name"]
            let typeInfo = 
                match reader.["system_type_id"] |> unbox |> finBySqlEngineTypeId with
                | Some x -> x
                | None -> failwithf "Cannot map column %s of sql engine type %O to CLR/SqlDbType type." name reader.["system_type_id"]

            yield { 
                Column.Name = name
                Ordinal = unbox reader.["column_ordinal"]
                ClrTypeFullName =  typeInfo.ClrTypeFullName
                IsNullable = unbox reader.["is_nullable"]
            }
    ] 

    member internal this.ExtractParameters(connectionString, commandText, commandType) =  [
            use conn = new SqlConnection(connectionString)
            conn.Open()

            match commandType with 

            | CommandType.StoredProcedure ->
                //quick solution for now. Maybe better to use conn.GetSchema("ProcedureParameters")
                use cmd = new SqlCommand(commandText, conn, CommandType = CommandType.StoredProcedure)
                SqlCommandBuilder.DeriveParameters cmd
                for p in cmd.Parameters do
                    // typeName is full namespace, ie AdventureWorks2012.dbo.myTableType. Only need the last part.
                    let udt = if String.IsNullOrEmpty(p.TypeName) then "" else p.TypeName.Split('.') |> Seq.last
                    match findTypeInfoByProviderType(p.SqlDbType, udt) with
                    | Some x -> 
                        yield { 
                            Name = p.ParameterName
                            TypeInfo = x 
                            IsNullable = p.IsNullable
                            Direction = p.Direction 
                        }
                    | None -> 
                        failwithf "Cannot map pair of SqlDbType '%O' and user definto type '%s' CLR type." p.SqlDbType udt

            | CommandType.Text -> 
                use cmd = new SqlCommand("sys.sp_describe_undeclared_parameters", conn, CommandType = CommandType.StoredProcedure)
                cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
                use reader = cmd.ExecuteReader()
                while(reader.Read()) do

                    let paramName = string reader.["name"]
                    let sqlEngineTypeId = unbox<int> reader.["suggested_system_type_id"]

                    let udtName = Convert.ToString(value = reader.["suggested_user_type_name"])
                    let direction = 
                        let output = unbox reader.["suggested_is_output"]
                        let input = unbox reader.["suggested_is_input"]
                        if input && output then ParameterDirection.InputOutput
                        elif output then ParameterDirection.Output
                        else ParameterDirection.Input
                    
                    let typeInfo = 
                        match finBySqlEngineTypeIdAndUdt(sqlEngineTypeId, udtName) with
                        | Some x -> x
                        | None -> failwithf "Cannot map sql engine type %i and UDT %s to CLR/SqlDbType type. Parameter name: %s" sqlEngineTypeId udtName paramName

                    yield { 
                            Name = paramName
                            TypeInfo = typeInfo 
                            IsNullable = false
                            Direction = direction 
                    }

            | _ -> failwithf "Unsupported command type: %O" commandType    
        ]

    member internal __.AddPropertiesForParameters(parameters: Parameter list) = [
        for p in parameters do
            let name = p.Name
            assert name.StartsWith("@")

            if not p.TypeInfo.IsTvpType 
            then 
                let propertyName = if p.Direction = ParameterDirection.ReturnValue then "SpReturnValue" else name.Substring 1
                let propertyType = p.TypeInfo.ClrType
                let prop = ProvidedProperty(propertyName, propertyType = propertyType)
                if p.Direction = ParameterDirection.Output || p.Direction = ParameterDirection.InputOutput || p.Direction = ParameterDirection.ReturnValue
                then 
                    assert (not p.TypeInfo.IsTvpType)
                    prop.GetterCode <- fun args -> 
                        <@@ 
                            let sqlCommand : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                            sqlCommand.Parameters.[name].Value
                        @@>
  
                if p.Direction = ParameterDirection.Input
                then 
                    prop.SetterCode <- fun args -> 
                        <@@ 
                            let sqlCommand : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                            sqlCommand.Parameters.[name].Value <- %%Expr.Coerce(args.[1], typeof<obj>)
                        @@>
                yield prop :> MemberInfo
            else
                let propertyName = name.Substring 1

                let columns = p.TypeInfo.TvpColumns |> Seq.toArray
                //still some duplication in generating list of tuples type. Overlaps with output columns case
                let columnCount = columns.Length
                assert (columnCount > 0)

                let rowType = 
                    if columns.Length = 1 
                    then columns.[0].ClrTypeConsideringNullable
                    else this.PrepareTupleTypeForColumns columns

                let prop = ProvidedProperty(propertyName, propertyType = typedefof<_ seq>.MakeGenericType rowType)
                let mapper = p.TypeInfo.TvpColumns |> Seq.toList |> List.map (fun c -> c.ClrTypeFullName, c.IsNullable) |> List.unzip |> QuotationsFactory.MapOptionsToObjects 
                prop.SetterCode <- fun args -> 
                    <@@
                        ()
                        let sqlCommand : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                        let table = new DataTable();

                        for i = 0 to columnCount - 1 do
                            table.Columns.Add() |> ignore

                        let input = %%Expr.Coerce(args.[1], typeof<Collections.IEnumerable>) |> Seq.cast<obj>
                        for row in input do
                            let values = if columnCount = 1 then [|box row|] else FSharpValue.GetTupleFields row
                            (%%mapper : obj[] -> unit) values
                            table.Rows.Add values |> ignore 

                        sqlCommand.Parameters.[name].Value <- table
                    @@>
                yield upcast prop 
        ]

    member internal __.GetExecuteNonQuery() = 
        let body (args : Expr list) =
            <@@
                async {
                    let sqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>) : SqlCommand
                    //open connection async on .NET 4.5
                    sqlCommand.Connection.Open()
                    use ensureConnectionClosed = sqlCommand.CloseConnectionOnly()
                    return! sqlCommand.AsyncExecuteNonQuery() 
                }
            @@>
        typeof<int>, body

    member internal __.AddExecuteMethod(outputColumns: _ list, providedCommandType: ProvidedTypeDefinition, resultType, singleRow, commandText) = 
            
        let syncReturnType, executeMethodBody = 
            if outputColumns.IsEmpty
            then 
                this.GetExecuteNonQuery()
            elif resultType = ResultType.DataTable
            then 
                this.DataTable(providedCommandType, commandText, outputColumns, singleRow)
            else
                let rowType, executeMethodBody = 
                    match outputColumns with
                    | [ col ] -> 
                        let column0Type = col.ClrTypeConsideringNullable
                        column0Type, QuotationsFactory.GetBody("SelectOnlyColumn0", column0Type, singleRow, col)
                    | _ -> 
                        if resultType = ResultType.Tuples 
                        then 
                            this.Tuples(outputColumns, singleRow)
                        else 
                            assert (resultType = ResultType.Records)
                            this.Records(providedCommandType, outputColumns, singleRow)

                let returnType = if singleRow then rowType else ProvidedTypeBuilder.MakeGenericType(typedefof<_ seq>, [ rowType ])
                           
                returnType, executeMethodBody
                    
        let asyncReturnType = ProvidedTypeBuilder.MakeGenericType(typedefof<_ Async>, [ syncReturnType ])
        let asyncExecute = ProvidedMethod("AsyncExecute", [], asyncReturnType, InvokeCode = executeMethodBody)
        let execute = ProvidedMethod("Execute", [], syncReturnType)
        execute.InvokeCode <- fun args ->
            let runSync = ProvidedTypeBuilder.MakeGenericMethod(typeof<Async>.GetMethod("RunSynchronously"), [ syncReturnType ])
            let callAsync = Expr.Call (Expr.Coerce (args.[0], providedCommandType), asyncExecute, [])
            Expr.Call(runSync, [ Expr.Coerce (callAsync, asyncReturnType); Expr.Value option<int>.None; Expr.Value option<CancellationToken>.None ])

        providedCommandType.AddMembers [ asyncExecute; execute ]

    member internal this.Tuples(columns, singleRow) =
        let tupleType = this.PrepareTupleTypeForColumns columns

        let rowMapper = 
            let values = Var("values", typeof<obj[]>)
            let getTupleType = Expr.Call(typeof<Type>.GetMethod("GetType", [| typeof<string>|]), [ Expr.Value tupleType.AssemblyQualifiedName ])
            Expr.Lambda(values, Expr.Coerce(Expr.Call(typeof<FSharpValue>.GetMethod("MakeTuple"), [ Expr.Var values; getTupleType ]), tupleType))

        tupleType, QuotationsFactory.GetBody("GetTypedSequence", tupleType, rowMapper, singleRow, columns)

    member internal this.PrepareTupleTypeForColumns(columns : seq<Column>) : Type = 
        FSharpType.MakeTupleType [| for c in columns -> c.ClrTypeConsideringNullable|]
        
    member internal this.Records(providedCommandType, columns, singleRow) =
        let recordType = ProvidedTypeDefinition("Record", baseType = Some typeof<obj>, HideObjectMethods = true)
        for col in columns do
            if col.Name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." col.Ordinal

            let property = ProvidedProperty(col.Name, propertyType = col.ClrTypeConsideringNullable)
            property.GetterCode <- fun args -> 
                <@@ 
                    let values : obj[] = %%Expr.Coerce(args.[0], typeof<obj[]>)
                    values.[%%Expr.Value (col.Ordinal - 1)]
                @@>

            recordType.AddMember property

        providedCommandType.AddMember recordType
        let getExecuteBody (args : Expr list) = 
            let columnTypes, isNullableColumn = columns |> List.map (fun c -> c.ClrTypeFullName, c.IsNullable) |> List.unzip 
            QuotationsFactory.GetTypedSequence(args.[0], <@ fun(values : obj[]) -> box values @>, singleRow, columns)
                         
        upcast recordType, getExecuteBody
    
    member internal this.DataTable(providedCommandType, commandText, outputColumns, singleRow) =
        let rowType = ProvidedTypeDefinition("Row", Some typeof<DataRow>)
        for col in outputColumns do
            let name = col.Name
            if col.Name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." col.Ordinal

            let nakedType = Type.GetType col.ClrTypeFullName

            let property = 
                if col.IsNullable 
                then
                    ProvidedProperty(
                        name, 
                        propertyType= typedefof<_ option>.MakeGenericType nakedType,
                        GetterCode = QuotationsFactory.GetBody("GetNullableValueFromRow", nakedType, name),
                        SetterCode = QuotationsFactory.GetBody("SetNullableValueInRow", nakedType, name)
                    )
                else
                    ProvidedProperty(
                        name, 
                        propertyType = nakedType, 
                        GetterCode = (fun args -> <@@ (%%args.[0] : DataRow).[name] @@>),
                        SetterCode = fun args -> <@@ (%%args.[0] : DataRow).[name] <- %%Expr.Coerce(args.[1],  typeof<obj>) @@>
                    )

            rowType.AddMember property

        providedCommandType.AddMember rowType

        let body = QuotationsFactory.GetBody("GetTypedDataTable",  typeof<DataRow>, singleRow)
        let returnType = typedefof<_ DataTable>.MakeGenericType rowType

        returnType, body

