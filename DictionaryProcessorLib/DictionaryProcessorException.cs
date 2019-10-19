﻿using System;
using System.Runtime.Serialization;

namespace DictionaryProcessorLib
{
	public class DictionaryProcessorException : Exception
	{
		public DictionaryProcessorException() { }

		public DictionaryProcessorException(string message) : base(message) { }

		public DictionaryProcessorException(string message, Exception inner) : base(message, inner) { }

		protected DictionaryProcessorException(SerializationInfo info, StreamingContext context) : base(info, context) { }
	}
}