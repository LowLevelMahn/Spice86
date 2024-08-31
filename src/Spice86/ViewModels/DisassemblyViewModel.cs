namespace Spice86.ViewModels;

using Avalonia.Collections;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

using Iced.Intel;

using Spice86.Core.Emulator.CPU;
using Spice86.Core.Emulator.Memory;
using Spice86.Core.Emulator.VM;
using Spice86.Core.Emulator.VM.Breakpoint;
using Spice86.Infrastructure;
using Spice86.MemoryWrappers;
using Spice86.Messages;
using Spice86.Models.Debugging;
using Spice86.Shared.Utils;

public partial class DisassemblyViewModel : ViewModelBase, IRecipient<UpdateViewMessage> {
    private readonly IMemory _memory;
    private readonly State _state;
    private readonly IMessenger _messenger;
    private readonly IPauseHandler _pauseHandler;
    private readonly ITextClipboard _textClipboard;
    private readonly EmulatorBreakpointsManager _emulatorBreakpointsManager;
    private readonly IUIDispatcher _uiDispatcher;

    [ObservableProperty] private string _header = "Disassembly View";

    [ObservableProperty] private AvaloniaList<CpuInstructionInfo> _instructions = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateDisassemblyCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToCsIpCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewDisassemblyViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLineCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepIntoCommand))]
    private bool _isPaused;

    [ObservableProperty] private int _numberOfInstructionsShown = 50;

    private uint? _startAddress;

    public uint? StartAddress {
        get => _startAddress;
        set {
            Header = value is null ? "" : $"0x{value:X}";
            SetProperty(ref _startAddress, value);
        }
    }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CloseTabCommand))]
    private bool _canCloseTab;

    public DisassemblyViewModel(IMemory memory, State state, IPauseHandler pauseHandler, IUIDispatcher uiDispatcher,
        IMessenger messenger, ITextClipboard textClipboard, EmulatorBreakpointsManager emulatorBreakpointsManager,
        bool canCloseTab = false) {
        _messenger = messenger;
        _uiDispatcher = uiDispatcher;
        _textClipboard = textClipboard;
        _memory = memory;
        _state = state;
        _pauseHandler = pauseHandler;
        _emulatorBreakpointsManager = emulatorBreakpointsManager;
        IsPaused = pauseHandler.IsPaused;
        pauseHandler.Pausing += () => _uiDispatcher.Post(() => IsPaused = true);
        pauseHandler.Resumed += () => _uiDispatcher.Post(() => IsPaused = false);
        CanCloseTab = canCloseTab;
        UpdateInstructions();
    }

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private void CloseTab() => _messenger.Send(new RemoveViewModelMessage<DisassemblyViewModel>(this));

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void StepInto() {
        BreakPoint stepBreakPoint =
            new UnconditionalBreakPoint(BreakPointType.EXECUTION, OnStepBreakPointReached, true);
        _emulatorBreakpointsManager.ToggleBreakPoint(stepBreakPoint, true);
        _uiDispatcher.Post(() => _pauseHandler.Resume());
    }

    private void OnStepBreakPointReached(BreakPoint _) =>
        _uiDispatcher.Post(() => {
            _pauseHandler.RequestPause("DisassemblyViewModel Step into button command");
            GoToCsIp();
        });

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void NewDisassemblyView() {
        DisassemblyViewModel disassemblyViewModel = new(_memory, _state, _pauseHandler, _uiDispatcher, _messenger,
            _textClipboard,
            _emulatorBreakpointsManager, canCloseTab: true) {
            IsPaused = IsPaused
        };
        _messenger.Send(new AddViewModelMessage<DisassemblyViewModel>(disassemblyViewModel));
    }

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void GoToCsIp() {
        StartAddress = _state.IpPhysicalAddress;
        UpdateDisassembly();
    }

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void UpdateDisassembly() {
        if (StartAddress is null) {
            return;
        }
        List<CpuInstructionInfo> instructions =
            DecodeInstructions(_state, _memory, StartAddress.Value, NumberOfInstructionsShown);
        Instructions.Clear();
        Instructions.AddRange(instructions);
        SelectedInstruction = Instructions.FirstOrDefault();
    }

    [ObservableProperty]
    private CpuInstructionInfo? _selectedInstruction;

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private async Task CopyLine() {
        if (SelectedInstruction is not null) {
            await _textClipboard.SetTextAsync(SelectedInstruction.StringRepresentation).ConfigureAwait(false);
        }
    }

    private static List<CpuInstructionInfo> DecodeInstructions(State state, IMemory memory, uint startAddress,
        int numberOfInstructionsShown) {
        CodeReader codeReader = CreateCodeReader(memory, out CodeMemoryStream emulatedMemoryStream);
        using CodeMemoryStream codeMemoryStream = emulatedMemoryStream;
        Decoder decoder = InitializeDecoder(codeReader, startAddress);
        int byteOffset = 0;
        codeMemoryStream.Position = startAddress;
        var instructions = new List<CpuInstructionInfo>();
        while (instructions.Count < numberOfInstructionsShown) {
            long instructionAddress = codeMemoryStream.Position;
            decoder.Decode(out Instruction instruction);
            CpuInstructionInfo instructionInfo = new() {
                Instruction = instruction,
                Address = (uint)instructionAddress,
                Length = instruction.Length,
                IP16 = instruction.IP16,
                IP32 = instruction.IP32,
                MemorySegment = instruction.MemorySegment,
                SegmentPrefix = instruction.SegmentPrefix,
                IsStackInstruction = instruction.IsStackInstruction,
                IsIPRelativeMemoryOperand = instruction.IsIPRelativeMemoryOperand,
                IPRelativeMemoryAddress = instruction.IPRelativeMemoryAddress,
                SegmentedAddress =
                    ConvertUtils.ToSegmentedAddressRepresentation(state.CS, (ushort)(state.IP + byteOffset)),
                FlowControl = instruction.FlowControl,
                Bytes = $"{Convert.ToHexString(memory.GetData((uint)instructionAddress, (uint)instruction.Length))}"
            };
            instructionInfo.StringRepresentation =
                $"{instructionInfo.Address:X4} ({instructionInfo.SegmentedAddress}): {instruction} ({instructionInfo.Bytes})";
            if (instructionAddress == state.IpPhysicalAddress) {
                instructionInfo.IsCsIp = true;
            }
            instructions.Add(instructionInfo);
            byteOffset += instruction.Length;
        }

        return instructions;
    }

    private static Decoder InitializeDecoder(CodeReader codeReader, uint currentIp) {
        Decoder decoder = Decoder.Create(16, codeReader, currentIp,
            DecoderOptions.Loadall286 | DecoderOptions.Loadall386);
        return decoder;
    }

    private static CodeReader CreateCodeReader(IMemory memory, out CodeMemoryStream codeMemoryStream) {
        codeMemoryStream = new CodeMemoryStream(memory);
        CodeReader codeReader = new StreamCodeReader(codeMemoryStream);
        return codeReader;
    }

    public void Receive(UpdateViewMessage message) => UpdateInstructions();

    private void UpdateInstructions() {
        if (StartAddress is not null) {
            UpdateDisassembly();
        } else {
            StartAddress = _state.IpPhysicalAddress;
            GoToCsIp();
        }
    }
}