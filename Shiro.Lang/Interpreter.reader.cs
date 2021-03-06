﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Web.Script.Serialization;
using Shiro.Guts;

namespace Shiro
{
    public partial class Interpreter
    {
        private Token ScanJSONDictionary(Dictionary<string, object> dict)
        {
            Token retVal = new Token();
            retVal.Children = new List<Token>();

            foreach (var key in dict.Keys)
            {
                if (dict[key] is Dictionary<string, object>)
                {
                    var innerObj = ScanJSONDictionary((Dictionary<string, object>) dict[key]);
                    retVal.Children.Add(new Token(key, innerObj.Children));
                }
                else
                {
                    object o = dict[key].ToString().TypeCoerce();
                    if (o is string && o.ToString().Trim().StartsWith("("))
                        retVal.Children.Add(Scan(o as string).SetName(key));
                    else 
                        retVal.Children.Add(new Token(key, o));
                }
            }

            return retVal;
        }

        private Token ScanInlineObject(string code, bool includesCurlies = false)
        {
            //"Escape" lambdas as property values (ie {f: (print "Hello world")}
            var jsonTemp = includesCurlies ? code : "{" + code + "}";
            var json = "";
            int depthCount = 0;
            char stringDelim = '#';

            for (var i = 0; i < jsonTemp.Length; i++)
            {
                var c = jsonTemp[i];
                if (c == '(' && stringDelim == '#')
                {
                    if (depthCount == 0 && !json.Trim().EndsWith("->"))
                        json += "\"(";
                    else
                        json += "(";
                    depthCount += 1;
                }
                else if (c == ')' && stringDelim == '#')
                {
                    depthCount -= 1;
                    if (depthCount == 0 && !jsonTemp.LookAhead(i, "->"))
                        json += ")\"";
                    else
                        json += ")";
                }
                else if (c == '"')
                {
                    if (depthCount > 0)
                        json += "\\\"";
                    else
                        json += c;

                    if (stringDelim == '"')
                        stringDelim = '#';
                    else
                        stringDelim = '"';
                }
                else if (c == '\'')
                {
                    json += c;

                    if (stringDelim == '\'')
                        stringDelim = '#';
                    else
                        stringDelim = '\'';
                }
                else if (c == '`')
                {
                    json += c;

                    if (stringDelim == '`')
                        stringDelim = '#';
                    else
                        stringDelim = '`';
                }
                else
                    json += c;
            }

            if (stringDelim != '#')
                Error("Unterminated string in inline-object: " + json);

            try
            {
                var jss = new JavaScriptSerializer();
                var dict = (Dictionary<string, object>)jss.DeserializeObject(json);
                var retVal = ScanJSONDictionary(dict);
                return retVal;
            }
            catch (Exception ex)
            {
                Error("Invalid inline object, could not parse it.  JSON error was: " + ex.Message);
                return Token.Nil;
            }
        }

        internal Token Scan(string code)
        {
            var retVal = new List<Token>();
            code = code.Trim();

            var work = "";
            var blockDepth = 0;
            var objectDepth = 0;
            char stringDelim = '#';
            bool isAutoV = false,
                 isAutoLambda = false;

            Action appendWork = () =>
            {
                decimal d;
                long l;

                if (!string.IsNullOrEmpty(work))
                {
                    if (isAutoV)
                    {
                        isAutoV = false;
                        retVal.Add(new Token(new Token[] { new Token("v"), new Token(work) }));
                    }
                    else if (isAutoLambda)
                        Error("The element following an auto-lambda must be a list, not " + work);
                    else if (work == "nil")
                        retVal.Add(Token.Nil);
                    else if (work == "true" || work == "True" || work == "TRUE")
                        retVal.Add(Token.True);
                    else if (work == "false" || work == "False" || work == "FALSE")
                        retVal.Add(Token.False);
                    else if (!decimal.TryParse(work, out d) && !long.TryParse(work, out l) && work.Contains(".") && !work.StartsWith("."))
                    {
                        //Reader shortcut for dot unrolling
                        var eles = work.Split('.');
                        List<Token> tokes = new List<Token>();
                        tokes.Add(new Token("."));
                        tokes.Add(new Token(new Token[] { new Token("v"), new Token(eles[0]) }));
                        for (var i = 1; i < eles.Length; i++)
                            tokes.Add(new Token(eles[i]));

                        retVal.Add(new Token(tokes.ToArray()));
                    }
                    else
                        retVal.Add(new Token(work));
                }
            };

            for (var i = 0; i < code.Length; i++)
            {
                var c = code[i];

                if (stringDelim != '#')
                {
                    if (c == '%' && code[i + 1] == stringDelim)
                    {
                        i += 1;
                        work += stringDelim.ToString();
                        continue;
                    }
                    else if (c == '%' && code[i + 1] == 's')
                    {
                        i += 1;
                        work += " ";
                        continue;
                    }
                    else if (c == '%' && code[i + 1] == 't')
                    {
                        i += 1;
                        work += "\t";
                        continue;
                    }
                    else if (c == '%' && code[i + 1] == 'n')
                    {
                        i += 1;
                        work += Environment.NewLine;
                        continue;
                    }
                    else if (c == '%' && code[i + 1] == '%')
                    {
                        i += 1;
                        work += '%';
                        continue;
                    }
                    else if (c == stringDelim)
                    {
                        var wasAutoInterp = stringDelim == '`';
                        if (blockDepth > 0)
                        {
                            work += stringDelim;
                            stringDelim = '#';
                            continue;
                        }
                        else if (!wasAutoInterp)
                            retVal.Add(new Token(work));
                        else
                            retVal.Add(new Token(new Token[] { new Token("interpolate"), new Token(work) }));
                        stringDelim = '#';
                        work = "";
                        continue;
                    }

                    work += c;
                    continue;
                }

                if (blockDepth > 0)
                {
                    if (c == ';')
                    {
                        try
                        {
                            while (code[++i] != ';' && code[i] != '\r' && code[i] != '\n')
                            { }
                        }
                        catch (IndexOutOfRangeException)
                        { }
                        continue;
                    }
                    if (c == '"' || c == '`')
                    {
                        stringDelim = c;
                        work += c;
                        continue;
                    }
                    else if (c == '\'')
                    {
                        if (code[i + 1] != '(')
                        {
                            stringDelim = c;
                            work += c;
                            continue;
                        }
                        else
                        {
                            work += c;
                            continue;
                        }
                    }
                    else if (c == '(')
                        blockDepth += 1;
                    else if (c == ')')
                    {
                        if (blockDepth == 1)
                        {
                            blockDepth = 0;

                            //reader shortcut:  Auto-lets
                            if (work.Trim().StartsWith("["))
                                work = $"(let {work.Replace('[', '(').Replace("]", ") (")}))";

                            var scanned = Scan(work);
                            if(!isAutoLambda)
                                retVal.Add(scanned);
                            else
                            {
                                isAutoLambda = false;
                                var paramList = retVal[retVal.Count - 1];
                                retVal.RemoveAt(retVal.Count - 1);

                                if (!scanned.IsParent)
                                    Error("The element following an auto-lamda arrow must be a list, not " + scanned.ToString());

                                retVal.Add(new Token(new Token[] { new Token("fn"), paramList, scanned }));
                            }
                            work = "";
                            continue;
                        }

                        blockDepth -= 1;
                    }

                    work += c;
                    continue;
                }

                switch (c)
                {
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                        appendWork();
                        work = "";
                        break;

                    case '(':
                        appendWork();
                        work = "";
                        blockDepth = 1;
                        break;

                    case ')':
                        Error("Unmatched end-paren found");
                        break;

                    case '{':
                        var almostJson = "";
                        try
                        {
                            while (code[++i] != '}' || objectDepth > 0)
                            {
                                almostJson += code[i];
                                if (code[i] == '{')
                                    objectDepth += 1;
                                if (code[i] == '}')
                                    objectDepth -= 1;
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            Error("Unterminated inline object (read: " + almostJson + ")");
                        }

                        retVal.Add(ScanInlineObject(almostJson));
                        break;

                    case ';':
                        appendWork();
                        try
                        {
                            while (code[++i] != ';' && code[i] != '\r' && code[i] != '\n')
                            {  }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            //No error, the last syntactical element can be a comment
                        }
                        break;

                    case '"':
                        appendWork();
                        stringDelim = c;
                        break;

                    case '\'':
                        if (code[i + 1] != '(')
                        {
                            //It's a string!
                            appendWork();
                            stringDelim = c;
                        }
                        else
                        {
                            //Quoted list, ie '(1 2 3) => (quote 1 2 3)
                            appendWork();

                            work = "quote ";
                            blockDepth = 1;
                            i++;
                        }
                        break;

                    //Reader shortcut:  auto-interpolation
                    case '`':
                        appendWork();
                        stringDelim = c;
                        break;

                    case '$':
                        appendWork();
                        isAutoV = true;
                        break;

                    //Reader shortcut:  Arrow Lambdas
                    case '-':
                        if (code[i + 1] == '>')
                        {
                            appendWork();
                            i += 1;

                            if (!retVal[retVal.Count - 1].IsParent)
                                Error("Arrow Lambda shortcut must be preceded by parameter list, not " + retVal[retVal.Count - 1].ToString());

                            isAutoLambda = true;
                        }
                        else
                            work += c;
                        break;

                    default:
                        work += c;
                        break;
                }
            }

            if (stringDelim != '#')
                Error("Unterminated string value: " + work);

            appendWork();

            if (blockDepth > 0)
                Error($"Unterminated list (missing {blockDepth} end-parens)");

            return new Token(retVal);
        }
    }
}
