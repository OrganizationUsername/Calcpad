﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Web;

namespace Calcpad.Core
{
    public class ExpressionParser
    {
        private enum Keyword
        {
            None,
            Hide,
            Show,
            Pre,
            Post,
            Val,
            Equ,
            Noc,
            Deg,
            Rad,
            Gra,
            Repeat,
            If,
            ElseIf,
            Else,
            EndIf,
            Loop,
            Break,
            Continue,
            Local,
            Global,
            Round,
            Pause,
            Input
        }

        private enum KeywordResult
        {
            None,
            Continue,
            Break
        }
        private int _isVal;
        private bool _isPausedByUser;
        private int _startLine;
        private int _pauseCharCount;
        private Keyword _previousKeyword = Keyword.None;
        private MathParser _parser;
        private ConditionParser _condition;
        private readonly StringBuilder _sb = new(10000);
        private readonly Stack<Loop> _loops = new();
        public Settings Settings { get; set; }
        public string HtmlResult { get; private set; }
        public static bool IsUs
        {
            get => Unit.IsUs;
            set => Unit.IsUs = value;
        }
        public bool IsPaused => _startLine > 0;
        public bool Debug { get; set; }
        public bool ShowWarnings { get; set; } = true;
        public ExpressionParser()
        {
            Settings = new Settings();
        }

        private static readonly string[] KeywordStrings;
        static ExpressionParser()
        {
            KeywordStrings = Enum.GetNames(typeof(Keyword));
            for (int i = 0, len = KeywordStrings.Length; i < len; ++i)
                KeywordStrings[i] = '#' + KeywordStrings[i].ToLowerInvariant();

            KeywordStrings[(int)Keyword.ElseIf] = "#else if";
            KeywordStrings[(int)Keyword.EndIf] = "#end if";
        }

        public void Parse(string sourceCode, bool calculate = true) =>
            Parse(sourceCode.AsSpan(), calculate);

        private static Keyword GetKeyword(ReadOnlySpan<char> s)
        {
            for (int i = 1, len = KeywordStrings.Length; i < len; ++i)
                if (s.StartsWith(KeywordStrings[i], StringComparison.OrdinalIgnoreCase))
                    return (Keyword)i;

            return Keyword.None;
        }

        public void Cancel() => _parser?.Cancel();
        public void Pause() => _isPausedByUser = true;

        private void Parse(ReadOnlySpan<char> code, bool calculate = true)
        {
            var errors = new Queue<int>();
            if (!calculate)
                _startLine = 0;

            if (_startLine == 0)
            {
                _parser = new MathParser(Settings.Math)
                {
                    ShowWarnings = ShowWarnings
                };
                _sb.Clear();
                _condition = new();
                _loops.Clear();
                _isVal = 0;
                _parser.SetVariable("Units", new Value(UnitsFactor()));
                _previousKeyword = Keyword.None;
            }
            else
            {
                var n = _sb.Length - _pauseCharCount;
                if (n > 0)
                    _sb.Remove(_pauseCharCount, n);
            }
            _parser.IsEnabled = calculate;
            var lines = new List<int> { 0 };
            var len = code.Length;
            for (int i = 0; i < len; ++i)
                if (code[i] == '\n')
                    lines.Add(i + 1);

            var lineCount = lines.Count - 1;
            var line = _startLine - 1;
            var isVisible = true;
            var s = ReadOnlySpan<char>.Empty;
            try
            {
                while (++line < lineCount)
                {
                    var expr = code[lines[line]..lines[line + 1]];
                    var eolIndex = expr.IndexOf('\v');
                    if (eolIndex > -1)
                    {
                        _parser.Line = int.Parse(expr[(eolIndex + 1)..]);
                        expr = expr[..eolIndex];
                    }
                    else
                        _parser.Line = line + 1;

                    var htmlId = string.Empty;
                    if (Debug && (_loops.Count == 0 || _loops.Peek().Iteration == 1))
                        htmlId = $" id=\"line-{line + 1}\" class=\"line\"";
                        
                    if (_parser.IsCanceled)
                        break;

                    s = expr.Trim();
                    if (s.IsEmpty)
                    {
                        if (isVisible)
                            _sb.AppendLine($"<p{htmlId}>&nbsp;</p>");

                        continue;
                    }
                    var result = ParseKeyword(s, htmlId, out Keyword keyword);
                    if (result == KeywordResult.Continue)
                        continue;
                    else if (result == KeywordResult.Break)
                        break;

                    if (!ParsePlot(s, htmlId))
                    {
                        if (ParseCondition(s, keyword, htmlId))
                            ParseExpression(s, keyword, htmlId);
                    }
                }
                ApplyUnits(_sb, calculate);
                if (line == lineCount && (calculate || !IsPaused))
                {
                    if (_condition.Id > 0)
#if BG
                        _sb.Append(ErrHtml($"Грешка: Условният \"#if\" блок не е затворен. Липсва \"#end if\".", line));
#else
                        _sb.Append(ErrHtml($"Error: \"#if\" block not closed. Missing \"#end if\".", line));
#endif
                    if (_loops.Count != 0)
#if BG
                        _sb.Append(ErrHtml($"Грешка: Блокът за цикъл \"#repeat\" не е затворен. Липсва \"#loop\".", line));
#else
                        _sb.Append(ErrHtml($"Error: \"#repeat\" block not closed. Missing \"#loop\".", line));
#endif
                    if (Debug && (_condition.Id > 0 || _loops.Count != 0))
                        errors.Enqueue(line);
                }
            }
            catch (MathParser.MathParserException ex)
            {
                AppendError(s.ToString(), ex.Message, line);
            }
            catch (Exception ex)
            {
#if BG
                _sb.Append(ErrHtml($"Неочаквана грешка: {ex.Message} Моля проверете коректността на израза.", line));
#else
                _sb.Append(ErrHtml($"Unexpected error: {ex.Message} Please check the expression consistency.", line));
#endif
                if (Debug)
                    errors.Enqueue(line);
            }
            finally
            {
                if (line == lineCount && calculate)
                    _startLine = 0;

                if (_startLine > 0)
#if BG
                    _sb.Append($"<p><span class=\"err\">Пауза!</span> натиснете <b>F5</b> за да <a href=\"#0\" data-text=\"continue\">продължите</a> или <b>Esc</b> за да <a href=\"#0\" data-text=\"cancel\">прекъснете</a>.</p>");
#else
                    _sb.Append($"<p><span class=\"err\">Paused!</span> Press <b>F5</b> to <a href=\"#0\" data-text=\"continue\">continue</a> or <b>Esc</b> to <a href=\"#0\" data-text=\"cancel\">cancel</a>.</p>");
#endif

                if (Debug && lineCount > 30 && errors.Count != 0)
                {
                    if (errors.Count == 1)
#if BG
                        _sb.AppendLine("<div class=\"errorHeader\">Общо <b>1</b> грешка на ред:");
#else
                        _sb.AppendLine("<div class=\"errorHeader\">Found <b>1</b> error on line:");
#endif
                    else
#if BG
                        _sb.AppendLine($"<div class=\"errorHeader\">Общо <b>{errors.Count}</b> грешки на редове:");
#else
                        _sb.AppendLine($"<div class=\"errorHeader\">Found <b>{errors.Count}</b> errors on lines:");
#endif
                    var count = 0;
                    var prevLine = 0;
                    while (errors.Count != 0 && count < 20)
                    {
                        var errLine = errors.Dequeue() + 1;
                        if (errLine != prevLine)
                        {
                            ++count;
                            _sb.Append($" <span class=\"roundBox\" data-line=\"{errLine}\">{errLine}</span>");
                        }
                        prevLine = errLine;
                    }
                    if (errors.Count > 0)
                        _sb.Append(" ...");

                    _sb.Append("</div>");
                    _sb.AppendLine("<style>body {padding-top:1em;}</style>");
                    errors.Clear();

                }
                HtmlResult = _sb.ToString();

                if (calculate && _startLine == 0)
                {
                    _parser.ClearCache();
                    _parser = null;
                }
            }

            void AppendError(string lineContent, string text, int line)
            {
#if BG
                _sb.Append(ErrHtml($"Грешка в \"{lineContent}\" на ред {LineHtml(line)}: {text}</p>", line));
#else
                _sb.Append(ErrHtml($"Error in \"{lineContent}\" on line {LineHtml(line)}: {text}</p>", line));
#endif
                if (Debug)
                    errors.Enqueue(line);
            }

            string ErrHtml(string text, int line) => $"<p class=\"err\"{Id(line)}\">{text}</p>";
            string LineHtml(int line) => $"[<a href=\"#0\" data-text=\"{line + 1}\">{line + 1}</a>]";
            string Id(int line) => Debug ? $" id=\"line-{line + 1}\"" : string.Empty;  
            KeywordResult ParseKeyword(ReadOnlySpan<char> s, string htmlId, out Keyword keyword)
            {
                keyword = Keyword.None;
                if (s[0] == '#' || _isPausedByUser)
                {
                    keyword = _isPausedByUser ? Keyword.Pause : GetKeyword(s);
                    if (keyword == Keyword.Hide)
                        isVisible = false;
                    else if (keyword == Keyword.Show)
                        isVisible = true;
                    else if (keyword == Keyword.Pre)
                        isVisible = !calculate;
                    else if (keyword == Keyword.Post)
                        isVisible = calculate;
                    else if (keyword == Keyword.Input)
                        return ParseKeywordInput();
                    else if (keyword == Keyword.Pause)
                        return ParseKeywordPause(htmlId);
                    else if (keyword == Keyword.Val)
                        _isVal = 1;
                    else if (keyword == Keyword.Equ)
                        _isVal = 0;
                    else if (keyword == Keyword.Noc)
                        _isVal = -1;
                    else if (keyword == Keyword.Deg)
                        _parser.Degrees = 0;
                    else if (keyword == Keyword.Rad)
                        _parser.Degrees = 1;
                    else if (keyword == Keyword.Gra)
                        _parser.Degrees = 2;
                    else if (keyword == Keyword.Round)
                        ParseKeywordRound(s);
                    else if (keyword == Keyword.Repeat)
                        ParseKeywordRepeat(s, htmlId);
                    else if (keyword == Keyword.Loop)
                        ParseKeywordLoop(s, htmlId);
                    else if (keyword == Keyword.Break)
                    {
                        if (ParseKeywordBreak(s, htmlId))
                            return KeywordResult.Break;
                    }
                    else if (keyword == Keyword.Continue)
                        ParseKeywordContinue(s, htmlId);
                    else if (keyword != Keyword.Global && keyword != Keyword.Local)
                        return KeywordResult.None;

                    return KeywordResult.Continue;
                }
                return KeywordResult.None;
            }

            KeywordResult ParseKeywordInput()
            {
                if (_condition.IsSatisfied)
                {
                    _previousKeyword = Keyword.Input;
                    if (calculate)
                    {
                        _startLine = line + 1;
                        _pauseCharCount = _sb.Length;
                        calculate = false;
                        return KeywordResult.Continue;
                    }
                    return KeywordResult.Break;
                }
                return calculate ? KeywordResult.Continue : KeywordResult.Break;
            }

            KeywordResult ParseKeywordPause(string htmlId)
            {
                if (_condition.IsSatisfied && (calculate || _startLine > 0))
                {
                    if (calculate)
                    {
                        if (_isPausedByUser)
                            _startLine = line;
                        else
                            _startLine = line + 1;
                    }

                    if (_previousKeyword != Keyword.Input)
                        _pauseCharCount = _sb.Length;

                    _previousKeyword = Keyword.Pause;
                    _isPausedByUser = false;
                    return KeywordResult.Break;
                }
                if (isVisible && !calculate)
                    _sb.Append($"<p{htmlId} class=\"cond\">#pause</p>");

                return KeywordResult.Continue;
            }

            void ParseKeywordRound(ReadOnlySpan<char> s)
            {
                if (s.Length > 6)
                {
                    var expr = s[6..].Trim();
                    if (int.TryParse(expr, out int n))
                        Settings.Math.Decimals = n;
                    else
                    {
                        try
                        {
                            _parser.Parse(expr);
                            _parser.Calculate();
                            Settings.Math.Decimals = (int)Math.Round(_parser.Real, MidpointRounding.AwayFromZero);
                        }
                        catch (MathParser.MathParserException ex)
                        {
                            AppendError(s.ToString(), ex.Message, line);
                        }
                    }
                }
            }

            void ParseKeywordRepeat(ReadOnlySpan<char> s, string htmlId)
            {
                ReadOnlySpan<char> expression = s.Length > 7 ?
                    s[7..].Trim() :
                    Span<char>.Empty;

                if (calculate)
                {
                    if (_condition.IsSatisfied)
                    {
                        var count = 0;
                        if (!expression.IsWhiteSpace())
                        {
                            try
                            {
                                _parser.Parse(expression);
                                _parser.Calculate();
                                if (_parser.Real > int.MaxValue)
#if BG
                                    AppendError(s.ToString(), $"Броят на итерациите е по-голям от максималния {int.MaxValue}.</p>", line));
#else
                                    AppendError(s.ToString(), $"Number of iterations exceeds the maximum {int.MaxValue}.</p>", line);
#endif
                                else
                                    count = (int)Math.Round(_parser.Real, MidpointRounding.AwayFromZero);
                            }
                            catch (MathParser.MathParserException ex)
                            {
                                AppendError(s.ToString(), ex.Message, line);
                            }
                        }
                        else
                            count = -1;

                        _loops.Push(new Loop(line, count, _condition.Id));
                    }
                }
                else if (isVisible)
                {
                    if (expression.IsWhiteSpace())
                        _sb.Append($"<p{htmlId} class=\"cond\">#repeat</p><div class=\"indent\">");
                    else
                    {
                        try
                        {
                            _parser.Parse(expression);
                            _sb.Append($"<p{htmlId}><span class=\"cond\">#repeat</span> {_parser.ToHtml()}</p><div class=\"indent\">");
                        }
                        catch (MathParser.MathParserException ex)
                        {
                            AppendError(s.ToString(), ex.Message, line);
                        }
                    }
                }
            }

            void ParseKeywordLoop(ReadOnlySpan<char> s, string htmlId)
            {
                if (calculate)
                {
                    if (_condition.IsSatisfied)
                    {
                        if (_loops.Count == 0)
#if BG
                            AppendError(s.ToString(), "\"#loop\" без съответен \"#repeat\".", line);
#else
                            AppendError(s.ToString(), "\"#loop\" without a corresponding \"#repeat\".", line);
#endif
                        else if (_loops.Peek().Id != _condition.Id)
#if BG
                            AppendError(s.ToString(), "Преплитане на \"#if - #end if\" и \"#repeat - #loop\" блокове.", line);
#else
                            AppendError(s.ToString(), "Entangled \"#if - #end if\" and \"#repeat - #loop\" blocks.", line);
#endif
                        else if (!_loops.Peek().Iterate(ref line))
                            _loops.Pop();
                    }
                }
                else if (isVisible)
                    _sb.Append($"</div><p{htmlId} class=\"cond\">#loop</p>");
            }

            bool ParseKeywordBreak(ReadOnlySpan<char> s, string htmlId)
            {
                if (calculate)
                {
                    if (_condition.IsSatisfied)
                    {
                        if (_loops.Count != 0)
                            _loops.Peek().Break();
                        else
                            return true;
                    }
                }
                else if (isVisible)
                    _sb.Append($"<p{htmlId} class=\"cond\">#break</p>");

                return false;
            }

            void ParseKeywordContinue(ReadOnlySpan<char> s, string htmlId)
            {
                const int RemoveCondition = Keyword.EndIf - Keyword.If;
                if (calculate)
                {
                    if (_condition.IsSatisfied)
                    {
                        if (_loops.Count == 0)
#if BG
                            AppendError(s.ToString(), "\"#continue\" без съответен \"#repeat\".", line);
#else
                            AppendError(s.ToString(), "\"#continue\" without a corresponding \"#repeat\".", line);
#endif
                        else
                        {
                            var loop = _loops.Peek();
                            while (_condition.Id > loop.Id)
                                _condition.SetCondition(RemoveCondition);
                            loop.Iterate(ref line);
                        }

                    }
                }
                else if (isVisible)
                    _sb.Append($"<p{htmlId} class=\"cond\">#continue</p>");
            }

            bool ParsePlot(ReadOnlySpan<char> s, string htmlId)
            {

                if (s.StartsWith("$plot", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("$map", StringComparison.OrdinalIgnoreCase))
                {
                    if (isVisible && (_condition.IsSatisfied || !calculate))
                    {
                        PlotParser plotParser;
                        if (s.StartsWith("$p", StringComparison.OrdinalIgnoreCase))
                            plotParser = new ChartParser(_parser, Settings.Plot);
                        else
                            plotParser = new MapParser(_parser, Settings.Plot);

                        try
                        {
                            _parser.IsPlotting = true;
                            var s1 = plotParser.Parse(s, calculate);
                            _sb.Append(InsertAttribute(s1, htmlId));
                            _parser.IsPlotting = false;
                        }
                        catch (MathParser.MathParserException ex)
                        {
                            AppendError(s.ToString(), ex.Message, line);
                        }
                    }
                    return true;
                }
                return false;
            }

            bool ParseCondition(ReadOnlySpan<char> s, Keyword keyword, string htmlId)
            {
                if (IsPaused && !calculate)
                {
                    _condition.SetCondition(-1);
                    return keyword == Keyword.None;
                }

                _condition.SetCondition(keyword - Keyword.If);
                if (_condition.IsSatisfied && (_loops.Count == 0 || !_loops.Peek().IsBroken) || !calculate)
                {
                    if (_condition.KeywordLength == s.Length)
                    {
                        if (_condition.IsUnchecked)
                            Throw.ConditionEmpty();

                        if (isVisible && !calculate)
                        {
                            if (keyword == Keyword.Else)
                                _sb.Append($"</div><p{htmlId}>{_condition.ToHtml()}</p><div class = \"indent\">");
                            else
                                _sb.Append($"</div><p{htmlId}>{_condition.ToHtml()}</p>");
                        }
                    }
                    else if (_condition.KeywordLength > 0 &&
                             _condition.IsFound &&
                             _condition.IsUnchecked &&
                             calculate)
                        _condition.Check(0.0);
                    else
                        return true;
                }
                return false;
            }

            void ParseExpression(ReadOnlySpan<char> s, Keyword keyword, string htmlId)
            {
                var kwdLength = _condition.KeywordLength;
                var tokens = GetInput(s[kwdLength..]);
                var lineType = tokens.Count != 0 ?
                    tokens[0].Type :
                    TokenTypes.Text;
                var isOutput = isVisible && (!calculate || kwdLength == 0);
                bool isIndent = keyword == Keyword.ElseIf || keyword == Keyword.EndIf;
                AppendHtmlLineStart(isOutput, lineType, isIndent, htmlId);
                if (isOutput)
                {
                    if (lineType == TokenTypes.Html)
                        tokens[0] = new Token(InsertAttribute(tokens[0].Value, htmlId), TokenTypes.Html);

                    if (kwdLength > 0 && !calculate)
                        _sb.Append(_condition.ToHtml());
                }
                ParseTokens(tokens, isOutput);
                AppendHtmlLineEnd(isOutput, lineType, keyword == Keyword.If);
                if (_condition.IsUnchecked)
                {
                    if (calculate)
                        _condition.Check(_parser.Result);
                    else
                        _condition.Check();
                }
            }

            void AppendHtmlLineStart(bool isOutput, TokenTypes lineType, bool isIndent, string htmlId)
            {
                if (isOutput)
                {
                    if (isIndent)
                        _sb.Append("</div>");

                    if (lineType == TokenTypes.Heading)
                        _sb.Append($"<h3{htmlId}>");
                    else if (lineType != TokenTypes.Html)
                        _sb.Append($"<p{htmlId}>");
                }
            }

            void ParseTokens(List<Token> tokens, bool isOutput)
            {
                foreach (var token in tokens)
                {
                    if (token.Type == TokenTypes.Expression)
                    {
                        try
                        {
                            _parser.Parse(token.Value);
                            if (calculate && _isVal > -1)
                                _parser.Calculate(isOutput);
                            else
                                _parser.DefineCustomUnits();

                            if (isOutput)
                            {
                                if (_isVal == 1 & calculate)
                                    _sb.Append(Complex.Format(_parser.Result, Settings.Math.Decimals, OutputWriter.OutputFormat.Html));
                                else
                                {
                                    var html = _parser.ToHtml();
                                    if (Settings.Math.FormatEquations)
                                    {
                                        var xml = _parser.ToXml();
                                        _sb.Append($"<span class=\"eq\" data-xml=\'{xml}\'>{html}</span>");
                                    }
                                    else
                                        _sb.Append($"<span class=\"eq\">{html}</span>");
                                }
                            }
                        }
                        catch (MathParser.MathParserException ex)
                        {
                            _parser.ResetStack();
                            string errText;
                            if (!calculate && token.Value.Contains('?'))
                                errText = token.Value.Replace("?", "<input type=\"text\" size=\"2\" name=\"Var\">");
                            else
                                errText = HttpUtility.HtmlEncode(token.Value);
#if BG
                            errText = $"Грешка в \"{errText}\" на ред {LineHtml(line)}: {ex.Message}";
#else
                            errText = $"Error in \"{errText}\" on line {LineHtml(line)}: {ex.Message}";
#endif  
                            _sb.Append($"<span class=\"err\"{Id(line)}>{errText}</span>");
                            if (Debug)
                                errors.Enqueue(line);
                        }
                    }
                    else if (isVisible)
                        _sb.Append(token.Value);
                }
            }

            void AppendHtmlLineEnd(bool isOutput, TokenTypes lineType, bool indent)
            {
                if (isOutput)
                {
                    if (lineType == TokenTypes.Heading)
                        _sb.Append("</h3>");
                    else if (lineType != TokenTypes.Html)
                        _sb.Append("</p>");

                    if (indent)
                        _sb.Append("<div class = \"indent\">");

                    _sb.AppendLine();
                }
            }
        }

        private static string InsertAttribute(ReadOnlySpan<char> s, string attr)
        {
            if (s.Length > 2 && s[0] == '<' && char.IsLetter(s[1]))
            {
                var i = s.IndexOf('>');
                if (i > 1)
                {
                    var j = i;
                    while (j > 1)
                    {
                        --j;
                        if (s[j] != ' ')
                        {
                            if (s[j] == '/')
                                i = j;

                            break;
                        }
                    };
                    return s[..i].ToString() + attr + s[i..].ToString();
                }
            }
            return s.ToString();
        }

        private void ApplyUnits(StringBuilder sb, bool calculate)
        {
            string unitsHtml = calculate ?
                Settings.Units :
                "<span class=\"Units\">" + Settings.Units + "</span>";

            long len = sb.Length;
            sb.Replace("%u", unitsHtml);
            if (calculate || sb.Length == len)
                return;

            sb.Insert(0, "<select id=\"Units\" name=\"Units\"><option value=\"m\"> m </option><option value=\"cm\"> cm </option><option value=\"mm\"> mm </option></select>");
        }

        private double UnitsFactor()
        {
            return Settings.Units switch
            {
                "mm" => 1000,
                "cm" => 100,
                "m" => 1,
                _ => 0
            };
        }

        private List<Token> GetInput(ReadOnlySpan<char> s)
        {
            var tokens = new List<Token>();
            var ts = new TextSpan(s);
            var currentSeparator = ' ';
            for (int i = 0, len = s.Length; i < len; ++i)
            {
                var c = s[i];
                if (c == '\'' || c == '\"')
                {
                    if (currentSeparator == ' ' || currentSeparator == c)
                    {
                        if (!ts.IsEmpty)
                            AddToken(tokens, ts.Cut(), currentSeparator);

                        ts.Reset(i + 1);
                        if (currentSeparator == c)
                            currentSeparator = ' ';
                        else
                            currentSeparator = c;
                    }
                    else if (currentSeparator != ' ')
                        ts.Expand();
                }
                else
                    ts.Expand();
            }
            if (!ts.IsEmpty)
                AddToken(tokens, ts.Cut(), currentSeparator);

            return tokens;
        }

        private void AddToken(List<Token> tokens, ReadOnlySpan<char> value, char separator)
        {
            var tokenValue = value.ToString();
            var tokenType = GetTokenType(separator);

            if (tokenType == TokenTypes.Expression)
            {
                if (value.IsWhiteSpace())
                    return;
            }
            else if (_isVal < 1)
            {
                if (tokens.Count == 0)
                    tokenValue += ' ';
                else
                    tokenValue = ' ' + tokenValue + ' ';
            }

            var token = new Token(tokenValue, tokenType);
            if (token.Type == TokenTypes.Text)
            {
                tokenValue = tokenValue.TrimStart();
                if (tokenValue.Length > 0 && tokenValue[0] == '<')
                    token.Type = TokenTypes.Html;
            }
            tokens.Add(token);
        }

        private static TokenTypes GetTokenType(char separator)
        {
            return separator switch
            {
                ' ' => TokenTypes.Expression,
                '\"' => TokenTypes.Heading,
                '\'' => TokenTypes.Text,
                _ => TokenTypes.Error,
            };
        }

        private struct Token
        {
            internal string Value { get; }
            internal TokenTypes Type;
            internal Token(string value, TokenTypes type)
            {
                Value = value;
                Type = type;
            }
        }

        private enum TokenTypes
        {
            Expression,
            Heading,
            Text,
            Html,
            Error
        }

        private class ConditionParser
        {
            private enum Types
            {
                None,
                If,
                ElseIf,
                Else,
                EndIf
            }
            private readonly struct Item
            {
                internal bool Value { get; }
                internal Types Type { get; }
                internal Item(bool value, Types type)
                {
                    Type = type;
                    Value = value;
                }
            }

            private int _count;
            private string _keyword;
            private int _keywordLength;
            private readonly Item[] _conditions = new Item[20];
            private Types Type => _conditions[Id].Type;
            internal int Id { get; private set; }
            internal bool IsUnchecked { get; private set; }
            internal bool IsSatisfied => _conditions[_count].Value;
            internal bool IsFound { get; private set; }
            internal int KeywordLength => _keywordLength;

            internal ConditionParser()
            {
                _conditions[0] = new Item(true, Types.None);
                _keyword = string.Empty;
            }
            private void Add(bool value)
            {
                ++Id;
                _conditions[Id] = new Item(value, Types.If);
                if (IsSatisfied)
                {
                    ++_count;
                    IsFound = false;
                }
            }

            private void Remove()
            {
                --Id;
                if (_count > Id)
                {
                    --_count;
                    IsFound = true;

                }
            }

            private void Change(bool value, Types type)
            {
                _conditions[Id] = new Item(value, type);
            }

            internal void SetCondition(int index)
            {
                if (index < 0 || index >= (int)Types.EndIf)
                {
                    if (_keywordLength > 0)
                    {
                        _keywordLength = 0;
                        _keyword = string.Empty;
                    }
                    return;
                }

                var type = (Types)(index + 1);
                _keywordLength = GetKeywordLength(type);
                _keyword = GetKeyword(type);
                IsUnchecked = type == Types.If || type == Types.ElseIf;
                if (type > Types.If && _count == 0)
                    Throw.ConditionNotInitialized();

                if (Type == Types.Else)
                {
                    if (type == Types.Else)
                        Throw.DuplicateElse();

                    if (type == Types.ElseIf)
                        Throw.ElseIfAfterElse();
                }
                switch (type)
                {
                    case Types.If:
                        Add(true);
                        break;
                    case Types.ElseIf:
                        Change(true, Types.If);
                        break;
                    case Types.Else:
                        Change(!IsFound, type);
                        break;
                    case Types.EndIf:
                        Remove();
                        break;
                }
            }

            internal void Check(Complex value)
            {
                if (!value.IsReal)
                    Throw.ConditionComplex();

                var d = value.Re;
                if (double.IsNaN(d) || double.IsInfinity(d))
                    Throw.ConditionResultInvalid(d.ToString());

                var result = Math.Abs(d) > 1e-12;
                if (result)
                    IsFound = true;
                Change(result, Type);
                IsUnchecked = false;
            }

            internal void Check() => IsUnchecked = false;

            public override string ToString() => _keyword;

            internal string ToHtml()
            {
                if (string.IsNullOrEmpty(_keyword))
                    return _keyword;
                return "<span class=\"cond\">" + _keyword + "</span>";
            }

            private static int GetKeywordLength(Types type)
            {
                return type switch
                {
                    Types.If => 3,
                    Types.ElseIf => 8,
                    Types.Else => 5,
                    Types.EndIf => 7,
                    _ => 0,
                };
            }
            private static string GetKeyword(Types type)
            {
                return type switch
                {
                    Types.If => "#if ",
                    Types.ElseIf => "#else if ",
                    Types.Else => "#else",
                    Types.EndIf => "#end if",
                    _ => string.Empty,
                };
            }
        }

        private class Loop
        {
            private readonly int _startLine;
            private int _iteration;
            internal int Id { get; }
            internal int Iteration => _iteration;
            internal Loop(int startLine, int count, int id)
            {
                _startLine = startLine;
                if (count < 0)
                    count = 100000;

                _iteration = count;
                Id = id;
            }

            internal bool Iterate(ref int currentLine)
            {
                if (_iteration <= 1)
                    return false;

                currentLine = _startLine;
                --_iteration;
                return true;
            }

            internal void Break() => _iteration = 0;

            internal bool IsBroken => _iteration == 0;
        }
    }
}
