// Trabalho Prático 02 – Sincronização de Relógios / Comunicação
// Implementação do Algoritmo de Berkeley em C# (.NET 8)
// Modo de uso:
//  - Servidor (coordenador):
//      dotnet run -- servidor --porta 5000 --intervalo 5 --outlier-ms 0
//  - Cliente (nó):
//      dotnet run -- cliente --servidor localhost:5000 --nome C1 --deriva-ppm 100 --imprimir-intervalo 2
//  Abra múltiplos terminais e rode vários clientes com nomes/derivas diferentes.
//  O "relógio" dos clientes é lógico (não altera o relógio do SO). Simulamos deriva via ppm (partes por milhão).
//  O coordenador coleta as diferenças, calcula a média e envia ajustes (offsets) conforme Berkeley.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Globalization;

public static class Programa
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            ExibirAjuda();
            return;
        }

        var modo = args[0].ToLowerInvariant();
        var parametros = InterpretadorArgs.ParseArgs(args.Skip(1).ToArray());

        if (modo == "servidor")
        {
            int porta = parametros.GetInt("porta", 5000);
            int intervaloSeg = parametros.GetInt("intervalo", 5);
            int limiteOutlierMs = parametros.GetInt("outlier-ms", 0);

            var relogioCoordenador = new RelogioLogico(nome: "Coordenador", derivaPpm: 0);
            var servidor = new ServidorCoordenador(IPAddress.Any, porta, relogioCoordenador,
                                                   TimeSpan.FromSeconds(intervaloSeg), limiteOutlierMs);
            Console.WriteLine($"[SERVIDOR] Escutando na porta {porta}. Intervalo de sincronização: {intervaloSeg}s. Limite outlier: {limiteOutlierMs} ms");
            await servidor.ExecutarAsync();
        }
        else if (modo == "cliente")
        {
            var enderecoServidor = parametros.GetString("servidor", "localhost:5000");
            var nome = parametros.GetString("nome", "Cliente");
            double derivaPpm = parametros.GetDouble("deriva-ppm", 0);
            int intervaloImpressaoSeg = parametros.GetInt("imprimir-intervalo", 2);

            var relogio = new RelogioLogico(nome, derivaPpm);
            var cliente = new NoCliente(enderecoServidor, relogio, TimeSpan.FromSeconds(intervaloImpressaoSeg));
            Console.WriteLine($"[CLIENTE {nome}] Conectando a {enderecoServidor} | deriva={derivaPpm} ppm");
            await cliente.ExecutarAsync();
        }
        else
        {
            ExibirAjuda();
        }
    }

    static void ExibirAjuda()
    {
        Console.WriteLine(@"Uso:
  Servidor:
    dotnet run -- servidor --porta 5000 --intervalo 5 --outlier-ms 0

  Cliente:
    dotnet run -- cliente --servidor localhost:5000 --nome C1 --deriva-ppm 100 --imprimir-intervalo 2

Notas:
 - 'deriva-ppm' simula a deriva do relógio lógico (ex.: 100 ppm => 0,01% mais rápido).
 - O servidor executa rodadas periódicas de sincronização Berkeley.
 - Este programa usa TCP e mensagens simples linha-a-linha.
");
    }
}

public class InterpretadorArgs
{
    private readonly Dictionary<string, string> _mapa = new(StringComparer.OrdinalIgnoreCase);
    public void Set(string chave, string valor) => _mapa[chave] = valor;
    public string GetString(string chave, string padrao) => _mapa.TryGetValue(chave, out var v) ? v : padrao;
    public int GetInt(string chave, int padrao) => int.TryParse(GetString(chave, padrao.ToString(CultureInfo.InvariantCulture)), out var x) ? x : padrao;
    public double GetDouble(string chave, double padrao) => double.TryParse(GetString(chave, padrao.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : padrao;

    public static InterpretadorArgs ParseArgs(string[] args)
    {
        var bag = new InterpretadorArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var chave = args[i].Substring(2);
                string? valor = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                bag.Set(chave, valor);
            }
        }
        return bag;
    }
}

public class RelogioLogico
{
    private readonly DateTime _baseUtc;
    private readonly double _fatorDeriva;
    private long _deslocamentoTicks;

    public string Nome { get; }

    public RelogioLogico(string nome, double derivaPpm)
    {
        Nome = nome;
        _baseUtc = DateTime.UtcNow;
        _fatorDeriva = 1.0 + (derivaPpm / 1_000_000.0);
        _deslocamentoTicks = 0;
    }

    public DateTime Agora()
    {
        var delta = DateTime.UtcNow - _baseUtc;
        var simulado = TimeSpan.FromTicks((long)(delta.Ticks * _fatorDeriva));
        return new DateTime(_baseUtc.Ticks + simulado.Ticks + _deslocamentoTicks, DateTimeKind.Utc);
    }

    public long AgoraTicks() => Agora().Ticks;

    public void AjustarTicks(long deltaTicks)
    {
        Interlocked.Add(ref _deslocamentoTicks, deltaTicks);
    }
}

public static class Protocolo
{
    public static string Ola(string nome) => $"OLA {nome}";
    public static string RequisitarTempo(long ticksServidor) => $"REQUISITAR_TEMPO {ticksServidor}";
    public static string Diferenca(long diffTicks) => $"DIFERENCA {diffTicks}";
    public static string Ajuste(long offsetTicks) => $"AJUSTE {offsetTicks}";
}

public class ServidorCoordenador
{
    private readonly TcpListener _escutador;
    private readonly RelogioLogico _relogio;
    private readonly TimeSpan _intervalo;
    private readonly int _outlierMs;

    private readonly List<ConexaoCliente> _clientes = new();
    private readonly object _lock = new();

    public ServidorCoordenador(IPAddress bind, int porta, RelogioLogico relogio, TimeSpan intervalo, int outlierMs)
    {
        _escutador = new TcpListener(bind, porta);
        _relogio = relogio;
        _intervalo = intervalo;
        _outlierMs = outlierMs;
    }

    public async Task ExecutarAsync()
    {
        _escutador.Start();
        _ = Task.Run(AceitarLoopAsync);
        await SincronizarLoopAsync();
    }

    private async Task AceitarLoopAsync()
    {
        while (true)
        {
            try
            {
                var tcp = await _escutador.AcceptTcpClientAsync();
                _ = Task.Run(() => TratarClienteAsync(tcp));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SERVIDOR] Erro ao aceitar: {ex.Message}");
            }
        }
    }

    private async Task TratarClienteAsync(TcpClient tcp)
    {
        using var cliente = tcp;
        using var stream = cliente.GetStream();
        using var leitor = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var escritor = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        var ola = await leitor.ReadLineAsync();
        if (ola == null || !ola.Trim().StartsWith("OLA "))
        {
            Console.WriteLine("[SERVIDOR] Mensagem OLA inválida, encerrando.");
            return;
        }
        var nome = ola.Substring(4).Trim();

        var conexao = new ConexaoCliente(nome, cliente, stream, leitor, escritor);
        lock (_lock) _clientes.Add(conexao);
        Console.WriteLine($"[SERVIDOR] Cliente conectado: {nome} ({cliente.Client.RemoteEndPoint})");

        try
        {
            while (cliente.Connected)
            {
                await Task.Delay(1000);
            }
        }
        finally
        {
            lock (_lock) _clientes.Remove(conexao);
            Console.WriteLine($"[SERVIDOR] Cliente desconectado: {nome}");
        }
    }

    private async Task SincronizarLoopAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(_intervalo);
                List<ConexaoCliente> snapshot;
                lock (_lock) snapshot = _clientes.ToList();
                if (snapshot.Count == 0)
                {
                    Console.WriteLine("[SERVIDOR] Nenhum cliente para sincronizar.");
                    continue;
                }

                Console.WriteLine($"\n[SERVIDOR] === Rodada Berkeley @ {_relogio.Agora():O} com {snapshot.Count} clientes ===");

                var diffs = new List<(ConexaoCliente? conexao, long diffTicks)> { (null, 0) };

                foreach (var c in snapshot)
                {
                    try
                    {
                        var ticksServidor = _relogio.AgoraTicks();
                        await c.Escritor.WriteLineAsync(Protocolo.RequisitarTempo(ticksServidor));
                        var linha = await c.Leitor.ReadLineAsync();
                        if (linha == null || !linha.StartsWith("DIFERENCA ")) throw new IOException("Mensagem DIFERENCA inválida");
                        var diffStr = linha.Substring(10).Trim();
                        if (!long.TryParse(diffStr, out var diffTicks)) throw new IOException("Falha ao interpretar DIFERENCA");
                        diffs.Add((c, diffTicks));
                        Console.WriteLine($"[SERVIDOR] DIFERENCA de {c.Nome}: {TimeSpan.FromTicks(diffTicks).TotalMilliseconds:F1} ms");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SERVIDOR] {c.Nome} falhou durante coleta: {ex.Message}");
                    }
                }

                if (diffs.Count <= 1)
                {
                    Console.WriteLine("[SERVIDOR] Nenhuma diferença coletada.");
                    continue;
                }

                if (_outlierMs > 0)
                {
                    var limiteTicks = TimeSpan.FromMilliseconds(_outlierMs).Ticks;
                    var filtrados = diffs.Where(d => Math.Abs(d.diffTicks) <= Math.Abs(limiteTicks) || d.conexao == null).ToList();
                    if (filtrados.Count >= 2) diffs = filtrados;
                }

                long media = (long)Math.Round(diffs.Average(d => (double)d.diffTicks));
                Console.WriteLine($"[SERVIDOR] Média das diferenças = {TimeSpan.FromTicks(media).TotalMilliseconds:F1} ms (n={diffs.Count})");

                foreach (var (conexao, diff) in diffs)
                {
                    if (conexao == null) continue;
                    long ajuste = media - diff;
                    try
                    {
                        await conexao.Escritor.WriteLineAsync(Protocolo.Ajuste(ajuste));
                        Console.WriteLine($"[SERVIDOR] -> {conexao.Nome}: AJUSTE {TimeSpan.FromTicks(ajuste).TotalMilliseconds:F1} ms");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SERVIDOR] Falha ao enviar AJUSTE para {conexao.Nome}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SERVIDOR] Erro na rodada: {ex.Message}");
            }
        }
    }

    private class ConexaoCliente
    {
        public string Nome { get; }
        public TcpClient Tcp { get; }
        public NetworkStream Stream { get; }
        public StreamReader Leitor { get; }
        public StreamWriter Escritor { get; }
        public ConexaoCliente(string nome, TcpClient tcp, NetworkStream stream, StreamReader leitor, StreamWriter escritor)
        {
            Nome = nome; Tcp = tcp; Stream = stream; Leitor = leitor; Escritor = escritor;
        }
    }
}

public class NoCliente
{
    private readonly string _hostServidor;
    private readonly int _portaServidor;
    private readonly RelogioLogico _relogio;
    private readonly TimeSpan _intervaloImpressao;

    public NoCliente(string servidor, RelogioLogico relogio, TimeSpan intervaloImpressao)
    {
        var partes = servidor.Split(':');
        _hostServidor = partes[0];
        _portaServidor = partes.Length > 1 ? int.Parse(partes[1]) : 5000;
        _relogio = relogio;
        _intervaloImpressao = intervaloImpressao;
    }

    public async Task ExecutarAsync()
    {
        while (true)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(_hostServidor, _portaServidor);
                using var stream = tcp.GetStream();
                using var leitor = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                using var escritor = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                await escritor.WriteLineAsync(Protocolo.Ola(_relogio.Nome));
                Console.WriteLine($"[CLIENTE {_relogio.Nome}] Conectado. Relógio lógico: {_relogio.Agora():O}");

                var impressor = Task.Run(async () =>
                {
                    while (tcp.Connected)
                    {
                        Console.WriteLine($"[CLIENTE {_relogio.Nome}] Agora = {_relogio.Agora():O}");
                        await Task.Delay(_intervaloImpressao);
                    }
                });

                while (tcp.Connected)
                {
                    var linha = await leitor.ReadLineAsync();
                    if (linha == null) break;

                    if (linha.StartsWith("REQUISITAR_TEMPO "))
                    {
                        var ticksServidorStr = linha.Substring("REQUISITAR_TEMPO ".Length).Trim();
                        if (!long.TryParse(ticksServidorStr, out var ticksServidor)) continue;
                        var ticksCliente = _relogio.AgoraTicks();
                        var diff = ticksCliente - ticksServidor;
                        await escritor.WriteLineAsync(Protocolo.Diferenca(diff));
                    }
                    else if (linha.StartsWith("AJUSTE "))
                    {
                        var ajusteStr = linha.Substring("AJUSTE ".Length).Trim();
                        if (long.TryParse(ajusteStr, out var delta))
                        {
                            _relogio.AjustarTicks(delta);
                            Console.WriteLine($"[CLIENTE {_relogio.Nome}] Aplicado AJUSTE {TimeSpan.FromTicks(delta).TotalMilliseconds:F1} ms; Agora={_relogio.Agora():O}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLIENTE {_relogio.Nome}] Erro de conexão: {ex.Message}. Reconectando em 2s...");
                await Task.Delay(2000);
            }
        }
    }
}
