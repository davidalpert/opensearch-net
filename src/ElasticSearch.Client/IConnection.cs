﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ElasticSearch.Client
{
	interface IConnection
	{
		void Get(string path, Action<ConnectionStatus> callback);
		ConnectionStatus GetSync(string path);
		ConnectionStatus PostSync(string path, string data);
	}
}
