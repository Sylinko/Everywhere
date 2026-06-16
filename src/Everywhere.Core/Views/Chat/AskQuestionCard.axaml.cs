using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Plugins;
using ShadUI;
using ZLinq;

namespace Everywhere.Views;

public partial class AskQuestionCard : Card
{
    public abstract partial class OptionWrapper : ObservableObject
    {
        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        [RelayCommand]
        private void Select() => IsSelected = true;
    }

    public sealed class NormalOptionWrapper(ChatPluginQuestionOption option) : OptionWrapper
    {
        public ChatPluginQuestionOption Option { get; } = option;
    }

    public sealed partial class FreeformOptionWrapper : OptionWrapper
    {
        [ObservableProperty]
        public partial string? FreeformText { get; set; }
    }

    public sealed class QuestionWrapper
    {
        public ChatPluginQuestion Question { get; }
        public SelectionMode SelectionMode { get; }
        public IDynamicLocaleKey? MultiSelectHintKey => Question.MultiSelect ? new DynamicLocaleKey(LocaleKey.ChatPlugin_MultiSelectHint) : null;
        public List<OptionWrapper> OptionWrappers { get; } = [];

        public QuestionWrapper(ChatPluginQuestion question)
        {
            Question = question;
            SelectionMode = question.MultiSelect ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Single | SelectionMode.Toggle;

            var hasPreSelected = false;
            if (question.Options is not null)
            {
                foreach (var option in question.Options)
                {
                    var isSelected = option.Recommended && (question.MultiSelect || !hasPreSelected);
                    if (isSelected) hasPreSelected = true;

                    OptionWrappers.Add(
                        new NormalOptionWrapper(option)
                        {
                            IsSelected = isSelected
                        });
                }
            }

            // Always allow freeform
            OptionWrappers.Add(new FreeformOptionWrapper());
        }
    }

    #region Properties

    public static readonly StyledProperty<IReadOnlyList<ChatPluginQuestion>?> QuestionsProperty =
        AvaloniaProperty.Register<AskQuestionCard, IReadOnlyList<ChatPluginQuestion>?>(nameof(Questions));

    public IReadOnlyList<ChatPluginQuestion>? Questions
    {
        get => GetValue(QuestionsProperty);
        set => SetValue(QuestionsProperty, value);
    }

    public static readonly StyledProperty<IRelayCommand<IReadOnlyList<ChatPluginQuestionAnswer>>?> CommandProperty =
        AvaloniaProperty.Register<AskQuestionCard, IRelayCommand<IReadOnlyList<ChatPluginQuestionAnswer>>?>(nameof(Command));

    public IRelayCommand<IReadOnlyList<ChatPluginQuestionAnswer>>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly DirectProperty<AskQuestionCard, IReadOnlyList<QuestionWrapper>?> WrappedQuestionsProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, IReadOnlyList<QuestionWrapper>?>(
            nameof(WrappedQuestions),
            o => o.WrappedQuestions);

    public IReadOnlyList<QuestionWrapper>? WrappedQuestions
    {
        get;
        private set => SetAndRaise(WrappedQuestionsProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, QuestionWrapper?> CurrentQuestionProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, QuestionWrapper?>(
            nameof(CurrentQuestion),
            o => o.CurrentQuestion);

    public QuestionWrapper? CurrentQuestion
    {
        get;
        private set => SetAndRaise(CurrentQuestionProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, string?> PageDisplayProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, string?>(
            nameof(PageDisplay),
            o => o.PageDisplay);

    public string? PageDisplay
    {
        get;
        private set => SetAndRaise(PageDisplayProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, bool> HasPreviousPageProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, bool>(
            nameof(HasPreviousPage),
            o => o.HasPreviousPage);

    public bool HasPreviousPage
    {
        get;
        private set => SetAndRaise(HasPreviousPageProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, bool> HasNextPageProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, bool>(
            nameof(HasNextPage),
            o => o.HasNextPage);

    public bool HasNextPage
    {
        get;
        set => SetAndRaise(HasNextPageProperty, ref field, value);
    }

    #endregion

    private int _currentIndex;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != QuestionsProperty) return;

        var questions = change.GetNewValue<IReadOnlyList<ChatPluginQuestion>?>();
        if (questions is null or { Count: 0 })
        {
            WrappedQuestions = null;
            CurrentQuestion = null;
            PageDisplay = null;
            return;
        }

        WrappedQuestions = questions.AsValueEnumerable().Select(q => new QuestionWrapper(q)).ToList();
        NavigateTo(0);
    }

    private void NavigateTo(int index)
    {
        if (WrappedQuestions is null) return;

        _currentIndex = index;
        CurrentQuestion = WrappedQuestions[index];
        HasPreviousPage = _currentIndex > 0;
        HasNextPage = _currentIndex < WrappedQuestions.Count - 1;
        PreviousPageCommand.NotifyCanExecuteChanged();

        PageDisplay = WrappedQuestions.Count <= 1 ? null : $"{index + 1} / {WrappedQuestions.Count}";
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private void PreviousPage()
    {
        if (_currentIndex > 0) NavigateTo(_currentIndex - 1);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (WrappedQuestions is null) return;

        if (_currentIndex < WrappedQuestions.Count - 1)
        {
            NavigateTo(_currentIndex + 1);
        }
        else if (Command is { } command)
        {
            var answers = new ChatPluginQuestionAnswer[WrappedQuestions.Count];
            for (var i = 0; i < WrappedQuestions.Count; i++)
            {
                var question = WrappedQuestions[i];

                var selected = new List<string>();
                string? freeformText = null;
                foreach (var optionWrapper in question.OptionWrappers)
                {
                    if (optionWrapper is NormalOptionWrapper { IsSelected: true, Option: { } option })
                    {
                        selected.Add(option.Content);
                    }
                    else if (optionWrapper is FreeformOptionWrapper { FreeformText: { Length: > 0 } freeform })
                    {
                        freeformText = freeform;
                        break;
                    }
                }

                answers[i] = new ChatPluginQuestionAnswer(selected, freeformText);
            }

            if (command.CanExecute(answers))
            {
                command.Execute(answers);
            }
        }
    }
}