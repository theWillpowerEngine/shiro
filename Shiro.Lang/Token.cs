﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiro
{
    public class Token
    {
        #region Core Tokens

        public static Token Nil => new Token()
        {
            Toke = null,
            Children = null
        };

        public static Token True => new Token()
        {
            Toke = "true",
            Children = null
        };

        public static Token False => new Token()
        {
            Toke = "false",
            Children = null
        };
        #endregion

        #region CTORs

        public Token(string s)
        {
            if (s == null)
            {
                Toke = null;
                Children = null;
                return;
            }

            Toke = s.TypeCoerce();
        }

        public Token(string name, List<Token> val)
        {
            Name = name;
            Children = val;
        }
        public Token(string name, object val)
        {
            Name = name;
            Toke = val;
        }

        public Token(List<Token> c)
        {
            Children = c;
        }
        public Token(params Token[] cs)
        {
            Children = new List<Token>(cs);
        }
        public Token()
        {
            Toke = null;
            Children = null;
        }
        #endregion

        public string Name;
        public object Toke;
        public List<Token> Children;
        public Guid LetTableId = Guid.Empty;
        public List<string> Params = null;

        public bool IsParent => Children != null;
        public bool IsNil => (Children == null && (Toke == null || (Toke is Token && (Toke as Token).IsNil)));
        public bool IsNumeric => Toke is long || Toke is decimal;
        public bool IsDecimal => Toke is decimal;
        public bool IsFunction => Params != null;

        public bool IsTrue {
            get
            {
                if (this == True) return true;
                if(Toke == True.Toke) return true;
                if(IsNumeric && (int)Toke != 0) return true;
                return false;
            }
        }

        public new string ToString()
        {
            if (IsNil)
                return "nil";
            if (Toke != null)
                return Toke.ToString();
            if (IsParent)
                return Children.ToFlatString();
            
            return "nil";
        }

        internal Token Eval(Interpreter merp)
        {
            if (IsParent)
                return merp.Eval(Children);
               
            return this;
        }

        public Token EvalLambda(Interpreter merp, params Token[] args)
        {
            if (!IsFunction) { 
                Interpreter.Error("Attempted to evaluate something as a lambda that's not a lambda.  It was: " + ToString());
                return Nil;
            }

            Guid letId = Guid.NewGuid();
            if (Params.Count != args.Length)
                Interpreter.Error($"Incorrect number of params passed to lambda, expected {Params.Count}, found {args.Length} instead.  The best I can tell you about the lambda is that it might look something like this: {ToString()}");

            int i = 0;
            foreach (var pn in Params)
                merp.Symbols.Let(pn, args[i++], letId);

            var retVal = Eval(merp);
            merp.Symbols.ClearLetId(letId);
            return retVal;
        }
    }
}