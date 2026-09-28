// Every Dapper call in hippo goes through Dapper.AOT's generated code, so native AOT never compiles Dapper's reflection.
[module: Dapper.DapperAot]
