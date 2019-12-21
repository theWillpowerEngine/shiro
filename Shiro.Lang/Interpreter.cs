﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Web.Script.Serialization;
using Shiro.Guts;
using Shiro.Interop;

namespace Shiro
{
    public partial class Interpreter
    {
        public static string Version = "0.2.0";
        internal Symbols Symbols;
        internal Loader Loader = new Loader();

        public Interpreter()
        {
            Symbols = new Symbols(this);
        }

        public static void Error(string msg)
        {
            throw new ApplicationException(msg);
        }

		public static Action<string> Output = s =>
		{
			Console.WriteLine(s);
		};

        public static Func<Interpreter, string, bool> LoadModule = DefaultModuleLoader;

        public static bool DefaultModuleLoader(Interpreter m, string s)
        {
            if (!File.Exists(s) && !File.Exists(s + ".shr"))
                return false;

            string code = "";
            if (File.Exists(s))
                code = File.ReadAllText(s);
            else
                code = File.ReadAllText(s + ".shr");

            m.Eval(code);
            return true;
        }

        public void RegisterAutoFunction(string name, Func<Interpreter, Token, Token> func)
        {
            Symbols.AddAutoFunc(name, func);
        }
        
        #region "Reader" (somewhere a LISP purist just threw up in their mouth and doesn't know why)

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
                        retVal.Children.Add(new Token(key, Scan(o.ToString()).Children));
                    else 
                        retVal.Children.Add(new Token(key, o));
                }
            }

            return retVal;
        }

        private Token ScanInlineObject(string code, bool includesCurlies = false)
        {
            //"Escape" lambdas as property values (ie {f: (print "Hello world")}
            var jsonTemp = includesCurlies ? code :  "{" + code + "}";
            var json = "";
            int depthCount = 0;
            for (var i = 0; i < jsonTemp.Length; i++)
            {
                var c = jsonTemp[i];
                if (c == '(')
                {
                    if (depthCount == 0)
                        json += "\"(";
                    else
                        json += "(";
                    depthCount += 1;
                }
                else if (c == ')')
                {
                    depthCount -= 1;
                    if (depthCount == 0)
                        json += ")\"";
                    else
                        json += ")";
                }
                else
                    json += c;
            }

            var jss = new JavaScriptSerializer();
            var dict = (Dictionary<string, object>)jss.DeserializeObject(json);
            var retVal = ScanJSONDictionary(dict);
            return retVal;
        }

        private Token Scan(string code)
        {
            var retVal = new List<Token>();
            code = code.Trim();

            var work = "";
            var blockDepth = 0;
            var objectDepth = 0;
            char stringDelim = '#';
            bool isAutoV = false;

            Action appendWork = () =>
            {
                if (!string.IsNullOrEmpty(work))
                {
                    if (isAutoV)
                    {
                        isAutoV = false;
                        retVal.Add(new Token(new Token[] {new Token("v"), new Token(work)}));
                    }
                    else if (work == "nil")
                        retVal.Add(Token.Nil);
                    else if (work == "true" || work == "True" || work == "TRUE")
                        retVal.Add(Token.True);
                    else if (work == "false" || work == "False" || work == "FALSE")
                        retVal.Add(Token.False);
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
                    else if (c == stringDelim)
                    {
                        stringDelim = '#';
                        retVal.Add(new Token(work));
                        work = "";
                        continue;
                    }

                    work += c;
                    continue;
                }

                if (blockDepth > 0)
                {
                    if (c == '(')
                        blockDepth += 1;
                    else if (c == ')')
                    {
                        if (blockDepth == 1)
                        {
                            blockDepth = 0;
                            retVal.Add(Scan(work));
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

                    case '$':
                        appendWork();
                        isAutoV = true;
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
        #endregion
    }
}