﻿using System;
using System.Runtime.Serialization;

namespace osu_patch.Lib.StringFixer.CLI
{
	public class StringFixerCliException : Exception
	{
		public StringFixerCliException() { }

		public StringFixerCliException(string message) : base(message) { }

		public StringFixerCliException(string message, Exception inner) : base(message, inner) { }

		protected StringFixerCliException(SerializationInfo info, StreamingContext context) : base(info, context) { }
	}
}