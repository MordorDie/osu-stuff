﻿using System;
using System.Runtime.Serialization;

namespace osu_patch.Lib.StringFixer
{
	public class StringFixerException : Exception
	{
		public StringFixerException() { }

		public StringFixerException(string message) : base(message) { }

		public StringFixerException(string message, Exception inner) : base(message, inner) { }

		protected StringFixerException(SerializationInfo info, StreamingContext context) : base(info, context) { }
	}
}