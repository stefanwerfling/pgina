﻿/*
	Copyright (c) 2011, pGina Team
	All rights reserved.

	Redistribution and use in source and binary forms, with or without
	modification, are permitted provided that the following conditions are met:
		* Redistributions of source code must retain the above copyright
		  notice, this list of conditions and the following disclaimer.
		* Redistributions in binary form must reproduce the above copyright
		  notice, this list of conditions and the following disclaimer in the
		  documentation and/or other materials provided with the distribution.
		* Neither the name of the pGina Team nor the names of its contributors 
		  may be used to endorse or promote products derived from this software without 
		  specific prior written permission.

	THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
	ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
	WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
	DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY
	DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
	(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
	LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
	ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
	(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
	SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

using log4net;

using pGina.Shared.Interfaces;
using pGina.Shared.Types;
using pGina.Shared.Settings;


namespace pGina.Plugin.RADIUS
{

    //Done: 
    //Keep track of sessionID, needs to be unique for each session, but be maintained through auth and accounting

    //Caled-Station-ID - MAC Addr
    //NAS-Identifier - Customizable

    //Session-Timeout - done
    //Return server response message - done


    //TODO:
    //Idle-Timeout - Unsure if possible / pgina's responsibility
    //Acct-Interim-Interval - radius accting, sends accting updates every x seconds
    //WISPr-Session-Terminate-Time := "2014-03-11T23:59:59"

   

    //(6:02:09 AM) emias: Oooska: Acct-Status-Type isn't set to "Start" or "Stop", but to some invalid values.  (No idea? Works fine here)
    //And the Acct-Unique-Session-Id isn't identical on login and logout. (Done)

    //Select network adapter?

    ////NAS-Port or NAS-Port-Type support? (accounting)

    //Stop using dictionary for Packet.cs

    //UI is till not quite 100% (force interim updates, timeout(def 2.50), ports)

    public class RADIUSPlugin : IPluginConfiguration, IPluginAuthentication, IPluginEventNotifications
    {
        private ILog m_logger = LogManager.GetLogger("RADIUSPlugin");
        public static Guid SimpleUuid = new Guid("{350047A0-2D0B-4E24-9F99-16CD18D6B142}");
        private string m_defaultDescription = "A RADIUS Authentication and Accounting Plugin";
        private dynamic m_settings = null;
        private Dictionary<Guid, Session> m_sessionManager;

        public RADIUSPlugin()
        {
            using(Process me = Process.GetCurrentProcess())
            {
                m_settings = new pGinaDynamicSettings(SimpleUuid);
                m_settings.SetDefault("ShowDescription", true);
                m_settings.SetDefault("Description", m_defaultDescription);

                m_sessionManager = new Dictionary<Guid, Session>();
                
                m_logger.DebugFormat("Plugin initialized on {0} in PID: {1} Session: {2}", Environment.MachineName, me.Id, me.SessionId);
            }            
        }        

        public string Name
        {
            get { return "RADIUS Plugin"; }
        }

        public string Description
        {
            get { return m_settings.Description; }
        }

        public string Version
        {
            get
            {
                return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
        }

        public Guid Uuid
        {
            get { return SimpleUuid; }
        }

        //Authenticates user
        BooleanResult IPluginAuthentication.AuthenticateUser(SessionProperties properties)
        {

            m_logger.DebugFormat("AuthenticateUser({0})", properties.Id.ToString());

            if (!(bool)Settings.Store.EnableAuth)
            {
                m_logger.Debug("Authentication stage set on RADIUS plugin but authentication is not enabled in plugin settings.");
                return new BooleanResult() { Success = false };
            }

            // Get user info
            UserInformation userInfo = properties.GetTrackedSingle<UserInformation>();

            try
            {
                RADIUSClient client = GetClient(); 
                bool result = client.Authenticate(userInfo.Username, userInfo.Password);
                if (result)
                {
                    Session session = new Session(properties.Id, userInfo.Username, client);
                    Packet p = client.lastReceievedPacket;

                    //Check for session timeout
                    if ((bool)Settings.Store.AllowSessionTimeout && p.containsAttribute(Packet.AttributeType.Session_Timeout))
                    {   
                        byte[] bTimeout = client.lastReceievedPacket.getRawAttribute(Packet.AttributeType.Session_Timeout);
                        Array.Reverse(bTimeout); //Curse you endianness
                        int seconds = BitConverter.ToInt32(bTimeout, 0);
                        session.SetSessionTimeout(seconds, SessionTimeoutCallback);
                        m_logger.DebugFormat("Setting timeout for {0} to {1} seconds.", userInfo.Username, seconds);
                    }

                    if (p.containsAttribute(Packet.AttributeType.Idle_Timeout))
                    {
                        byte[] bIdleTimeout = client.lastReceievedPacket.getRawAttribute(Packet.AttributeType.Idle_Timeout);
                        Array.Reverse(bIdleTimeout);
                        int seconds = BitConverter.ToInt32(bIdleTimeout, 0);
                        m_logger.DebugFormat("idle timeout value: {0}", seconds);
                    }

                    if ((bool)Settings.Store.WisprSessionTerminate && p.containsAttribute(Packet.AttributeType.Vendor_Specific))
                    {
                        //TODO:Wispr-Terminate-Time is vendor specific, first 3 bytes are vendor, 4th byte should be 9, then the timestamp string.
                        byte[] attr = p.getRawAttribute(Packet.AttributeType.Vendor_Specific);

                        m_logger.DebugFormat("Vendor-Specific bytes: {0}", BitConverter.ToString(attr));

                        byte[] vendor = new ArraySegment<byte>(attr, 0, 3).Array;

                        byte type = attr[3];

                        byte[] value = new ArraySegment<byte>(attr, 4, attr.Length - 4).Array;
                        string timestamp = System.Text.Encoding.UTF8.GetString(value);

                        m_logger.DebugFormat("Vendor specific attribute type. Vendor {0}, Type: {1}, Value: {2}", String.Join("-", vendor), type, timestamp);
                        
                        //TODO: Create callback based on timestamp value.
                    }

                    //Check for interim-update
                    if ((bool)Settings.Store.SendInterimUpdates)
                    {
                        int seconds = 0;

                        if (p.containsAttribute(Packet.AttributeType.Acct_Interim_Interval))
                        {
                            byte[] interval = client.lastReceievedPacket.getRawAttribute(Packet.AttributeType.Acct_Interim_Interval);
                            Array.Reverse(interval);
                            seconds = BitConverter.ToInt32(interval, 0);

                            m_logger.DebugFormat("Interim update seconds from packets: {0}", seconds);
                            
                        }

                        //Check to see if plugin is set to send interim updates more frequently
                        if ((bool)Settings.Store.ForceInterimUpdates)
                        {
                            int forceTime = (int)Settings.Store.InterimUpdateTime;
                            if (forceTime > 0 && forceTime < seconds)
                                seconds = forceTime;
                        }

                        //Set interim update
                        if (seconds > 0)
                        {
                            session.SetInterimUpdate(seconds, InterimUpdatesCallback);
                            m_logger.DebugFormat("Setting interim update interval for {0} to {1} seconds.", userInfo.Username, seconds);
                        }

                        else
                        {
                            m_logger.DebugFormat("Interim Updates are enabled, but no update interval was provided by the server or user.");
                        }
                        
                    }

                    lock (m_sessionManager)
                    {
                        m_logger.DebugFormat("Adding session to m_sessionManager. ID: {0}, session: {1}", session.id, session);
                        m_sessionManager.Add(session.id, session);
                    }

                    string message = null;
                    if (p.containsAttribute(Packet.AttributeType.Reply_Message))
                        message = p.getAttribute(Packet.AttributeType.Reply_Message);

                    return new BooleanResult() { Success = result, Message = message };
                }

                //Failure
                string msg = "Unable to validate username or password.";
                if (client.lastReceievedPacket != null
                    && client.lastReceievedPacket.containsAttribute(Packet.AttributeType.Reply_Message))
                {
                    msg = client.lastReceievedPacket.getAttribute(Packet.AttributeType.Reply_Message);
                }

                return new BooleanResult() { Success = result, Message = msg };
            }
            catch (RADIUSException re)
            {
                m_logger.Error("An error occurred during while authenticating.", re);
                return new BooleanResult() { Success = false, Message = re.Message };
            }
            catch (Exception e)
            {
                m_logger.Error("An unexpected error occurred while authenticating.", e);
                throw e;
            }
        }

        //Processes accounting on logon/logoff
        public void SessionChange(System.ServiceProcess.SessionChangeDescription changeDescription, pGina.Shared.Types.SessionProperties properties)
        {

            try
            {
                if (changeDescription.Reason != System.ServiceProcess.SessionChangeReason.SessionLogon
                    && changeDescription.Reason != System.ServiceProcess.SessionChangeReason.SessionLogoff)
                {
                    m_logger.DebugFormat("Not logging on or off for this session change call ({0})... exiting.", changeDescription.Reason);

                    return;
                }

                m_logger.DebugFormat("SessionChange({0})", properties.Id.ToString());

                if (!(bool)Settings.Store.EnableAcct)
                {
                    m_logger.Debug("Session Change stage set on RADIUS plugin but accounting is not enabled in plugin settings.");
                    return;
                }

                m_logger.DebugFormat("Checking username...");
                m_logger.DebugFormat("properties != null == {0}", properties != null);
                //Determine username (may change depending on value of UseModifiedName setting)
                string username = null;
                UserInformation ui = properties.GetTrackedSingle<UserInformation>();

                if (ui == null)
                {
                    m_logger.DebugFormat("No userinformation for this session logoff... exiting... (local machine login only?)");
                    return;
                }
                    

                if ((bool)Settings.Store.UseModifiedName)
                    username = ui.Username;
                else
                    username = ui.OriginalUsername;


                m_logger.DebugFormat("Session Change for user {0}", username);
                m_logger.DebugFormat("properties.id: {0}", properties.Id);
                m_logger.DebugFormat("m_sessionManager != null == {0}", m_sessionManager != null);

                Session session = null;

                //User is logging on
                if (changeDescription.Reason == System.ServiceProcess.SessionChangeReason.SessionLogon)
                {
                    lock (m_sessionManager)
                    {
                        //Check if session information is already available for this id
                        if (!m_sessionManager.Keys.Contains(properties.Id))
                        {
                            //No session info - must have authed with something other than RADIUS.
                            m_logger.DebugFormat("RADIUS Accounting Logon: Unable to find session for {0} with GUID {1}", username, properties.Id);

                            RADIUSClient client = GetClient();
                            session = new Session(properties.Id, username, client);

                            //Check forced interim-update setting
                            if (Settings.Store.SendInterimUpdates && (bool)Settings.Store.ForceInterimUpdates)
                            {
                                int interval = Settings.Store.InterimUpdateTime;
                                session.SetInterimUpdate(interval, InterimUpdatesCallback);
                            }
                        }

                        else
                            session = m_sessionManager[properties.Id];
                    }


                    //Determine which plugin authenticated the user (if any)
                    PluginActivityInformation pai = properties.GetTrackedSingle<PluginActivityInformation>();
                    Packet.Acct_Authentic authSource = Packet.Acct_Authentic.Not_Specified;
                    IEnumerable<Guid> authPlugins = pai.GetAuthenticationPlugins();
                    Guid LocalMachinePluginGuid = new Guid("{12FA152D-A2E3-4C8D-9535-5DCD49DFCB6D}");
                    foreach (Guid guid in authPlugins)
                    {
                        if (pai.GetAuthenticationResult(guid).Success)
                        {
                            if (guid == SimpleUuid)
                                authSource = Packet.Acct_Authentic.RADIUS;
                            else if (guid == LocalMachinePluginGuid)
                                authSource = Packet.Acct_Authentic.Local;
                            else //Not RADIUS, not Local, must be some other auth plugin
                                authSource = Packet.Acct_Authentic.Remote;
                            break;
                        }
                    }

                    //We can finally start the accounting process
                    try
                    {
                        lock (session)
                        {
                            session.windowsSessionId = changeDescription.SessionId; //Grab session ID now that we're authenticated
                            session.username = username; //Accting username may have changed depending on 'Use Modified username for accounting option'
                            session.client.startAccounting(username, authSource);

                            m_logger.DebugFormat("Successfully completed accounting start process...");
                        }
                    }
                    catch (Exception e)
                    {
                        m_logger.Error("Error occurred while starting accounting.", e);
                    }

                }

                
                //User is logging off
                else if (changeDescription.Reason == System.ServiceProcess.SessionChangeReason.SessionLogoff)
                {

                    m_logger.DebugFormat("RADIUS Session Change... logging off...");

                    lock (m_sessionManager)
                    {
                        if (m_sessionManager.Keys.Contains(properties.Id))
                            session = m_sessionManager[properties.Id];
                        else
                        {
                            m_logger.DebugFormat("Unable to find user info... next statement will cause crash... ?");
                            m_logger.DebugFormat("Users {0} is logging off, but no RADIUS session information is available for session ID {1}.", username, properties.Id);
                            return;
                        }

                        //Remove the session from the session manager
                        m_sessionManager.Remove(properties.Id);
                    }

                    lock (session)
                    {
                        m_logger.Debug("Disabling call backs...");
                        //Disbale any active callbacks for this session
                        session.disableCallbacks();

                        //Assume normal logout if no other terminate reason is listed.
                        if (session.terminate_cause == null)
                            session.terminate_cause = Packet.Acct_Terminate_Cause.User_Request;

                        m_logger.DebugFormat("Terminate cause: {0}", session.terminate_cause);

                        try
                        {
                            session.client.stopAccounting(session.username, session.terminate_cause);
                        }
                        catch (RADIUSException re)
                        {
                            m_logger.Debug("Error stopping accounting...");
                            m_logger.DebugFormat("Unable to send accounting stop message for user {0} with ID {1}. Message: {2}", session.username, session.id, re.Message);
                        }
                    }
                }

                m_logger.DebugFormat("All done session change... without any exceptions...");
            }
            catch (System.NullReferenceException e)
            {
                m_logger.DebugFormat("Caught nullreference exception. {0}", e.StackTrace);
                m_logger.DebugFormat("ChangeDescription.Reason: {0}", changeDescription.Reason);
                throw e;
            }
        }

        public void Configure()
        {
            Configuration conf = new Configuration();
            conf.ShowDialog();
        }

        public void Starting() 
        {
            if(m_sessionManager == null)
                m_sessionManager = new Dictionary<Guid, Session>();
        }
        public void Stopping() { }


        //Returns the client instantiated based on registry settings
        private RADIUSClient GetClient(string sessionId = null)
        {
            string[] servers = Regex.Split(Settings.Store.Server.Trim(), @"\s+");
            m_logger.DebugFormat("Servers: {0}", servers);
            int authport = Settings.Store.AuthPort;
            m_logger.DebugFormat("authport: {0}", authport);
            int acctport = Settings.Store.AcctPort;
            m_logger.DebugFormat("acctport: {0}", acctport);
            string sharedKey = Settings.Store.GetEncryptedSetting("SharedSecret");
            m_logger.DebugFormat("ss: {0}", sharedKey);
            int timeout = Settings.Store.Timeout;
            m_logger.DebugFormat("timeout: {0}", timeout);
            int retry = Settings.Store.Retry;
            m_logger.DebugFormat("retry: {0}", retry);


            byte[] ipAddr = null;
            string nasIdentifier = null;
            string calledStationId = null;
            
            if((bool)Settings.Store.SendNASIPAddress)
                ipAddr = getNetworkInfo().Item1;

            if((bool)Settings.Store.SendNASIdentifier){
                nasIdentifier = Settings.Store.NASIdentifier;
                nasIdentifier = nasIdentifier.Contains('%') ? replaceSymbols(nasIdentifier) : nasIdentifier;
            }

            if ((bool)Settings.Store.SendCalledStationID)
            {
                calledStationId = (String)Settings.Store.CalledStationID;
                calledStationId = calledStationId.Contains('%') ? replaceSymbols(calledStationId) : calledStationId;
            }
 
            RADIUSClient client = new RADIUSClient(servers, authport, acctport, sharedKey, timeout, retry, sessionId, ipAddr, nasIdentifier, calledStationId);
            return client;
        }

        private string replaceSymbols(string str)
        {
            Tuple<byte[], string> networkInfo = getNetworkInfo();
            return str.Replace("%macaddr", networkInfo.Item2)
                .Replace("%ipaddr", String.Join(".", networkInfo.Item1))
                .Replace("%computername", Environment.MachineName);
        }
        
        //Returns a tuple containing the current IPv4 address and mac address for the adapter
        //If ipAddressRegex is set, this will attempt to return the first address that matches the expression
        //Otherwise it returns the first viable IP address or 0.0.0.0 if no viable address is found. An empty
        //string is sent if no mac address is determined.
        private Tuple<byte[], string> getNetworkInfo()
        {
            string ipAddressRegex = Settings.Store.IPSuggestion;
           
            //Fallback values
            byte[] ipAddr = null;
            string macAddr = null;

            //Check each network adapter. 
            foreach(NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()){
                foreach (UnicastIPAddressInformation ipaddr in nic.GetIPProperties().UnicastAddresses)
                {   //Check to see if the NIC has any valid IP addresses.
                    if (ipaddr.Address.AddressFamily == AddressFamily.InterNetwork)
                        if (String.IsNullOrEmpty(ipAddressRegex) || //IP address, grab first adapter or check if it matches ip regex
                          Regex.Match(ipaddr.Address.ToString(), ipAddressRegex).Success)
                            return Tuple.Create(ipaddr.Address.GetAddressBytes(), nic.GetPhysicalAddress().ToString());
                        else if(ipAddr == null && macAddr == null){ //Fallback, grab info from first device
                            ipAddr = ipaddr.Address.GetAddressBytes();
                            macAddr = nic.GetPhysicalAddress().ToString();
                        }
                }
            }
            if (ipAddr == null) ipAddr = new byte[] { 0, 0, 0, 0 };
            if (macAddr == null) macAddr = "";
            return Tuple.Create(ipAddr, macAddr);
        }

        private void SessionTimeoutCallback(object state)
        {
            m_logger.DebugFormat("Session Timeout Callback called...");
            Session session = (Session)state;

            //Lock session? Might cause issues when we call LogoffSession on user and trigger the SessionChange method?
            if(!session.windowsSessionId.HasValue){
                m_logger.DebugFormat("Attempting to log user {0} out due to timeout, but no windows session ID is present for ID {1}", session.username, session.id);
                return;
            }

            if (session.terminate_cause != null)
            {
                m_logger.DebugFormat("User {0} has timed out, but terminate cause #{1} has already been set for ID {2}", session.username, session.terminate_cause, session.id);
            }
            session.terminate_cause = Packet.Acct_Terminate_Cause.Session_Timeout;

            m_logger.DebugFormat("Logging off user {0} in session{1} due to timeout.", session.username, session.windowsSessionId);
            bool result = Abstractions.WindowsApi.pInvokes.LogoffSession(session.windowsSessionId.Value);
            m_logger.DebugFormat("Log off {0}.", result ? "successful" : "failed");
        }

        private void SessionTerminateCallback(object state)
        {
            //Lock session? Might cause issues when we call LogoffSession on user and trigger the SessionChange method?
            Session session = (Session)state;
            session.terminate_cause = Packet.Acct_Terminate_Cause.Session_Timeout;

            if (!session.windowsSessionId.HasValue)
            {
                m_logger.DebugFormat("Attempting to log user {0} out due to WISPr Session limit, but no windows session ID is present for ID {1}", session.username, session.id);
                return;
            }

            if (session.terminate_cause != null)
            {
                m_logger.DebugFormat("User {0} has reached WISPr Session limit, but terminate cause #{1} has already been set for ID {2}", session.username, session.terminate_cause, session.id);
            }
            session.terminate_cause = Packet.Acct_Terminate_Cause.Session_Timeout;

            m_logger.DebugFormat("Logging off user {0} in session{1} due to timeout.", session.username, session.windowsSessionId);
            bool result = Abstractions.WindowsApi.pInvokes.LogoffSession(session.windowsSessionId.Value);
            m_logger.DebugFormat("Log off {0}.", result ? "successful" : "failed");
            
        }

        private void InterimUpdatesCallback(object state)
        {
            Session session = (Session)state;
            m_logger.DebugFormat("Sending interim-update for user {0}", session.username); 
            lock (session)
            {
                session.client.interimUpdate(session.username);
            }
        }
    }
}
