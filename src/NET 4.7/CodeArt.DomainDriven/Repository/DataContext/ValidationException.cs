using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.Transactions;

using CodeArt;

namespace CodeArt.DomainDriven
{
    public class ValidationException : DomainDrivenException
    {
        private ValidationResult _result;

        public ValidationResult Result
        {
            get { return _result; }
        }

        public ValidationException(ValidationResult result)
            : base(result.Message)
        {
            _result = result;
        }

    }
}
