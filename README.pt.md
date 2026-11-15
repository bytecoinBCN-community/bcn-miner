# BCN Miner para Windows

[English](README.md) · [Українська](README.uk.md) · [Español](README.es.md) · **Português** · [Türkçe](README.tr.md) · [Bahasa Indonesia](README.id.md) · [Tiếng Việt](README.vi.md) · [中文](README.zh.md)

Minerador de desktop para Bytecoin (BCN) que minera na CPU e em placas de vídeo NVIDIA, AMD e Intel Arc. Tem tema escuro e claro e interface em oito idiomas.

É um projeto independente da comunidade Bytecoin, sem ligação com os fundadores do Bytecoin nem com a bcndev.

![Tela de mineração](docs/screenshots/mining-dark.png)

## Download e execução

1. Baixe `BCNMiner-<versão>-win-portable.zip` em [bytecoin.community](https://bytecoin.community/#downloads) e descompacte onde quiser, inclusive num pendrive.
2. Execute `BCNMiner.exe` e clique em **Baixar motor** (na tela Dispositivos ou no aviso da tela Mineração). O minerador baixa a versão oficial do [XMRig](https://github.com/xmrig/xmrig) do GitHub, confere o SHA-256 e, para placas NVIDIA, adiciona o plugin xmrig-cuda compilado para o seu driver. Pela linha de comando, o mesmo é feito com `powershell -ExecutionPolicy Bypass -File .\get-engine.ps1`.

Não é preciso instalar nada: o .NET Framework 4.8 já vem com o Windows 10 e 11.

O Windows Defender costuma marcar mineradores como “software potencialmente indesejado”. Se o `xmrig.exe` sumir, adicione você mesmo a pasta `engine` às exclusões do Defender.

### Modo portátil

O zip da versão contém uma pasta `data`. Enquanto ela existir ao lado do `BCNMiner.exe`, as configurações, os registros e o cache de ajuste ficam nela, então a pasta inteira pode ir para outro PC. Apague `data` para guardar as configurações em `%APPDATA%\BCN Miner`. Configurações → Sobre mostra qual modo está ativo.

A opção “Iniciar com o Windows” grava no registro (`HKCU\...\Run`), e só quando você a liga.

## Como funciona

- **Interface:** `BCNMiner.exe`, WPF sobre .NET Framework 4.8.
- **Motor:** XMRig com o algoritmo `cn/0`, o CryptoNight original que o Bytecoin v3.5 (bloco v4) ainda usa. O minerador inicia o XMRig como processo filho, grava a configuração dele e lê as estatísticas pela API HTTP local (apenas `127.0.0.1`, token aleatório). Se o minerador fechar ou travar, o motor para junto.
- **Protocolo:** stratum no estilo Monero (`login` / `job` / `submit` / `keepalived`), o mesmo usado pelo bytecoin-gui e pela ponte [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum). O nonce fica no byte 39 do blob, onde tanto o Antminer X3 quanto o XMRig o esperam.

O minerador embutido no bytecoin-gui usa só a CPU. De lá vêm a lista de pools com reservas e a “dificuldade no login”; a mineração por GPU vem do XMRig.

**Sobre o `cn/0` no XMRig.** A autoconfiguração do XMRig desliga o `cn/0` de propósito em todos os backends, então uma configuração simples falha com `incompatible/disabled algorithm "cn/0"`. O alias `"cn/0": "cn"` também não resolve, porque o perfil `cn` ainda não existe quando a configuração é lida. Por isso, na primeira execução o minerador roda o motor uma vez (até 30 s) com salvamento automático. O motor ajusta sozinho o perfil `cn`: ele tem o mesmo scratchpad de 2 MB e os mesmos kernels do `cn/0` e leva em conta o tamanho do L3 e os parâmetros de cada GPU. O minerador copia esse perfil para a chave `cn/0` e o guarda em cache no `probe.json`. O ajuste é refeito ao clicar em Dispositivos → Verificar novamente, ou quando mudam as GPUs, os drivers ou a versão do motor. GPUs desligadas são simplesmente filtradas pelo índice.

## Placas de vídeo

| Fabricante | Backend | Gerações | Observações |
| --- | --- | --- | --- |
| NVIDIA | CUDA (plugin `xmrig-cuda.dll`) | Pascal (GTX 10), Turing (GTX 16, RTX 20), Ampere (RTX 30), Ada (RTX 40), Blackwell (RTX 50), RTX profissionais e GPUs de data center | As RTX 50 precisam de um plugin compilado com CUDA 12.8+; o minerador escolhe a versão mais nova que o seu driver suporta (CUDA 12.9 precisa do driver 527.41+, drivers antigos recebem 11.8 ou 10.2). Temperatura, consumo e ventoinha vêm do NVML. |
| AMD | OpenCL | GCN 4 (RX 400/500), Vega, RDNA 1–4 (RX 5000–9000), Radeon Pro, Instinct | Temperatura e consumo vêm do ADL. |
| Intel | OpenCL | Arc A (Alchemist), Arc B (Battlemage) | Experimental. Escolha Dispositivos → Plataforma OpenCL → Intel. |
| Gráficos integrados | — | Intel UHD/Iris, Radeon em APUs | Não são usados: dividem memória e refrigeração com a CPU. |

O XMRig suporta uma plataforma OpenCL por vez. No modo Auto o minerador escolhe AMD se houver uma Radeon dedicada; senão, Intel Arc. Cada placa pode ser desligada na tela Dispositivos; os botões aparecem após a primeira execução, quando o motor informa os números dos dispositivos.

Quando qualquer GPU atinge o limite (80 °C por padrão), a mineração pausa. Ela volta quando a placa esfria 8 °C.

![Tela de dispositivos](docs/screenshots/devices-light.png)

## Pool ou solo

- **Pool:** informe `host:porta` do pool principal e até dois pools reserva, além do seu endereço público BCN. O minerador nunca precisa de chaves ou da seed. Novas instalações começam com o pool da comunidade `pool.bytecoin.community:3333` (0% de taxa, PPLNS, <https://pool.bytecoin.community>); o botão “Pool da comunidade” em Pool e nó o recoloca como principal.
- **Solo no seu próprio nó:** o bytecoind v3.5 não oferece stratum aos mineradores, então a ponte [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) precisa rodar ao lado do nó. A recompensa vai para o endereço definido na ponte (`BCN_WALLET_ADDRESS`); o nome do worker só aparece no registro da ponte. Mantenha as portas 3333 e 8081 dentro da sua rede local.

Pool e nó → Testar conexão envia um único `login` e mostra a altura do bloco, o algoritmo e a dificuldade do share.

**Dificuldade do share.** No modo Auto o minerador envia `d=N` à ponte na senha, buscando cerca de um share a cada 30 s. Depois de iniciar, ele confere o hashrate e, se preciso, reinicia o motor com uma nova dificuldade. O mínimo da ponte é 1000.

## Perfis de carga

| Perfil | CPU | NVIDIA |
| --- | --- | --- |
| Eco | prioridade mínima | bfactor 10, pausas entre lotes |
| Equilibrado | prioridade abaixo do normal | bfactor 6 |
| Máximo | prioridade normal, sem concessões ao sistema | bfactor 2 |

O controle de threads de CPU define um limite superior. O CryptoNight precisa de 2 MB de L3 por thread, e o motor reduz o número de threads sozinho quando o cache é pequeno.

## Idiomas

Українська, English, Español, Português, Türkçe, Bahasa Indonesia, Tiếng Việt, 中文 — Configurações → Aparência → Idioma. A interface muda na hora; a mineração, o registro e o gráfico continuam rodando. “Do sistema” segue o idioma do Windows e, se ele não estiver disponível, usa o inglês.

As traduções ficam em `src/lang/<código>.txt` como linhas `chave = texto` (`{0}` é um marcador, `\n` uma quebra de linha) e são embutidas na compilação. Chaves ausentes vêm do inglês. Para corrigir uma tradução sem recompilar, coloque `<código>.txt` numa pasta `lang` ao lado do `BCNMiner.exe`; as linhas dele substituem as embutidas. Só o ucraniano e o inglês foram escritos à mão; os outros idiomas são traduções automáticas, e revisões de falantes nativos são bem-vindas.

## O que esperar

O Bytecoin é minerado por ASICs Antminer X3 (~220 kH/s em CryptoNight). Uma GPU moderna dá alguns kH/s em `cn/0`, e uma CPU, centenas de H/s. Aqui a mineração por GPU apoia a descentralização da rede mais do que dá lucro; compare o preço da energia com a recompensa antes de ligar várias placas.

O XMRig dedica 1 % do tempo a uma taxa para seus desenvolvedores; é o mínimo nas versões oficiais.

## Compilação

Basta o Windows 10/11, sem ferramentas de compilação:
```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
Em `dist\` ficam `BCNMiner.exe`, `get-engine.ps1`, o README e a licença. `.\build.ps1 -Release` também empacota `release\BCNMiner-<versão>-win-portable.zip`.

O código é C# 5, a versão da linguagem suportada pelo compilador que vem com o .NET Framework (`csc.exe`): sem `$"..."`, `?.` nem membros com corpo de expressão.

## Arquivos

- `src/App.cs` — inicialização, instância única, tema e idioma.
- `src/MainWindow.cs` — a janela e as cinco telas (Mineração, Pool e nó, Dispositivos, Registro, Configurações), bandeja, proteção contra superaquecimento, dificuldade automática.
- `src/Engine.cs` — configuração do XMRig, ajuste do `cn/0`, processo, API HTTP, Job Object.
- `src/Gpu.cs` — GPUs via WMI: fabricante, arquitetura, tamanho real da VRAM pelo registro.
- `src/Net.cs` — leitura de `host:porta`, teste de stratum, dificuldade a partir do target.
- `src/L.cs`, `src/lang/*.txt` — traduções.
- `src/Theme.cs`, `src/Styles.xaml`, `src/Ui.cs`, `src/Chart.cs` — sistema de design e gráfico.
- `get-engine.ps1` — download do motor com verificação SHA-256.

## Licença

O BCN Miner é distribuído sob a [GNU LGPL v3.0](LICENSE) (que complementa a [GNU GPL v3.0](COPYING)). XMRig e xmrig-cuda são GPL-3.0; não fazem parte deste repositório e o `get-engine.ps1` os baixa das versões oficiais.
