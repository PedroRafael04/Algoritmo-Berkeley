Membros: Pedro Rafael, Leonardo Dal'Olmo, Marlon Moser e Rafael Ehlert.

Para instanciar o processo servidor:

dotnet run -- servidor --port 5000 --interval 5 --outlier-ms 0

Para instanciar os processos clientes:

dotnet run -- cliente --server localhost:5000 --name C1 --drift-ppm 120 --print-interval 2
dotnet run -- cliente --server localhost:5000 --name C2 --drift-ppm -80 --print-interval 2
dotnet run -- cliente --server localhost:5000 --name C3 --drift-ppm 0 --print-interval 2

--drift-ppm simula relógio correndo mais rápido (positivo) ou mais lento (negativo).
