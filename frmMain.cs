using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using ServiceStack.Redis;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace WinhertzAppService
{
    public partial class frmMain : Form
    {
        private MqttClient mcMain;
        private RedisClient rcMain;
        private Thread thdTimer;
        private Thread thdCheckTime;
        private Thread thdFirmware;   //检查固件升级定时器

        private string SUBSCRIBE_REQUEST = "REQUEST";    //订阅请求主题
        private string SUBSCRIBE_STATUS = "STATUS";      //订阅状态数据
        private string SUBSCRIBE_COMMAND = "COMMAND";    //订阅控制指令


        //处理错误消息委托
        private delegate void ProcessErrorMessage(string Message);
        //处理网关数据委托
        private delegate void ProcessGatewayData(string GatewayID, string ID, string Value);
        //处理WEB数据委托
        private delegate void ProcessWebData(string GatewayID, string ID, string Value, string Delay);
        //处理请求数据委托
        private delegate void ProcessRequestData(string Type, string Name, string Value);


        //处理测试消息委托
        private delegate void ProcessTestMessage(string Message);

        public frmMain()
        {
            InitializeComponent();
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            try
            {
                //分布式缓存redis
                rcMain = new RedisClient("127.0.0.1", 6379);

                //MQTT
                mcMain = new MqttClient("127.0.0.1", 61613, false, null);
                string ClientID = Guid.NewGuid().ToString();
                mcMain.Connect(ClientID, "admin", "password");
                mcMain.MqttMsgPublishReceived += new MqttClient.MqttMsgPublishEventHandler(mcMain_MqttMsgPublishReceived);

                mcMain.Subscribe(new string[] { SUBSCRIBE_STATUS, SUBSCRIBE_COMMAND, SUBSCRIBE_REQUEST }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE, MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });

                //启动定时器线程
                thdTimer = new Thread(new ThreadStart(TimerWork));
                thdTimer.IsBackground = true;
                thdTimer.Start();

                //启动校时线程
                thdCheckTime = new Thread(new ThreadStart(CheckTimeWork));
                thdCheckTime.IsBackground = true;
                thdCheckTime.Start();

                //启动检查固件升级定时器
                thdFirmware = new Thread(new ThreadStart(FirmwareWork));
                thdFirmware.IsBackground = true;
                thdFirmware.Start();
            }
            catch (Exception E)
            {
                AddErrorLog(E.Message);
            }
        }

        private void FirmwareWork()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(10000);  //休眠5分钟

                    //先检查数据库中的升级标志
                    string sql = string.Format("SELECT * FROM sysconfig WHERE Name='IsUpdate' AND Value='1'");
                    DataTable dt = Common.GetDataTable(sql);
                    if (dt.Rows.Count >0)   //升级标志有效
                    {
                        sql = string.Format("SELECT * FROM sysconfig WHERE Name='UpdateDir'");
                        DataTable dtUpdateDir = Common.GetDataTable(sql);
                        if (dtUpdateDir.Rows.Count >0)
                        {
                            string UpdateDir = dtUpdateDir.Rows[0]["Value"].ToString();
                            Firmware fw = new Firmware();
                            fw.Path = UpdateDir;
                            fw.Load(UpdateDir);
                            string Version = fw.Version;

                            Byte[] Data = fw.GetBinary();

                            
                            //读数据库中网关的版本号
                            sql = string.Format("SELECT * FROM gateway WHERE Version != '{0}'", Version);
                            DataTable dtGateway = Common.GetDataTable(sql);
                            for (int i = 0; i<dtGateway.Rows.Count; i++)
                            {
                                string GatewayID = dtGateway.Rows[i]["ID"].ToString();
                                string GatewayName = dtGateway.Rows[i]["Name"].ToString();
                                string PhysicalID = dtGateway.Rows[i]["PhysicalID"].ToString();
                                string Topic = PhysicalID + "_" + "Update";
                                mcMain.Publish(Topic, Data, MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);
                                AddSystemLog("固件同步：" + GatewayName + "【物理序列号" + PhysicalID + "】");
                            }
                        }
                    }
                    
                }
                catch (Exception E)
                {
                    object[] tmpArray = new object[1];
                    tmpArray[0] = E.ToString();
                    this.BeginInvoke(new ProcessErrorMessage(InvokeFunofErrorMessage), tmpArray);
                }
            }
        }

        private void CheckTimeWork()
        {
            while (true)
            {
                try
                {
                    if (DateTime.Now.ToString("ss") == "35")
                    {
                        string sql = string.Format("SELECT * FROM gateway");
                        DataTable dt = Common.GetDataTable(sql);
                        for (int i = 0; i < dt.Rows.Count; i++)
                        {
                            string PhysicalID = dt.Rows[i]["PhysicalID"].ToString();
                            string GatewayID = dt.Rows[i]["ID"].ToString();
                            if (PhysicalID != string.Empty)
                            {
                                RequestData rd = new RequestData();
                                rd.Type = "CHECKTIME";
                                Content ct = new Content();
                                ct.Name = "TIME";
                                ct.Value = DateTime.Now.ToString();
                                rd.Data = ct;
                                mcMain.Publish(PhysicalID, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rd, Newtonsoft.Json.Formatting.Indented)), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);  //将查询到的网关序列号发送给网关
                                AddSystemLog("网关校时：" + GatewayID + "【物理序列号" + PhysicalID + "】");
                                Thread.Sleep(200);
                            }

                        }
                    }
                    Thread.Sleep(1000);  //休眠1分钟
                }
                catch (Exception E)
                {
                    object[] tmpArray = new object[1];
                    tmpArray[0] = E.Message;
                    this.BeginInvoke(new ProcessErrorMessage(InvokeFunofErrorMessage), tmpArray);
                }
            }
        }


        private void TimerWork()
        {
            int OldMinute = -1;
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                    int NewMinute = DateTime.Now.Minute;
                    if (NewMinute != OldMinute)
                    {
                        OldMinute = NewMinute;
                        //用1234567分别表示周一~周日
                        int intWeek = Convert.ToInt32(DateTime.Today.DayOfWeek);
                        if (intWeek == 0)  //周日
                        {
                            intWeek = 7;
                        }
                        //处理设备定时
                        string sql = string.Format("SELECT * FROM v_timer WHERE ExecuteTime = DATE_FORMAT(NOW(),'%H:%i')");
                        DataTable dt = Common.GetDataTable(sql);
                        for (int i = 0; i < dt.Rows.Count; i++)
                        {
                            string Week = dt.Rows[i]["Week"].ToString().Trim();
                            if (Week[intWeek - 1].ToString() == "1")  //星期满足条件
                            {
                                string GatewayID = dt.Rows[i]["GatewayID"].ToString().Trim();
                                string DeviceID = dt.Rows[i]["DeviceID"].ToString().Trim();
                                string PropertyName = dt.Rows[i]["PropertyName"].ToString().Trim();
                                string Value = dt.Rows[i]["Value"].ToString().Trim();
                                string ID = DeviceID + "." + PropertyName;
                                rcMain.Set(ID, Encoding.UTF8.GetBytes(Value));                                    //更新分布式缓存
                                ControlList clData = new ControlList();
                                clData.GatewayID = GatewayID;
                                ControlData[] d = new ControlData[1];
                                d[0] = new ControlData();
                                d[0].ID = ID;
                                d[0].Value = Value;
                                d[0].Delay = "0";   //马上执行
                                clData.Data = d;
                                mcMain.Publish(GatewayID, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(clData, Newtonsoft.Json.Formatting.Indented)), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);  //发送数据给网关
                                //显示执行记录
                                object[] tmpArray = new object[4];
                                tmpArray[0] = GatewayID;
                                tmpArray[1] = ID;
                                tmpArray[2] = Value;
                                tmpArray[3] = "0";
                                this.BeginInvoke(new ProcessWebData(InvokeFunofWebData), tmpArray);
                                //Thread.Sleep(200);
                            }
                        }
                        //处理模式定时
                        sql = string.Format("SELECT * FROM mode WHERE UseTimer ='1' AND ExecuteTime = DATE_FORMAT(NOW(),'%H:%i')");
                        DataTable dtMode = Common.GetDataTable(sql);
                        for (int i = 0; i < dtMode.Rows.Count; i++)
                        {
                            string Week = dtMode.Rows[i]["Week"].ToString().Trim();
                            if (Week[intWeek - 1].ToString() == "1")  //星期满足条件
                            {
                                string GatewayID = dtMode.Rows[i]["GatewayID"].ToString().Trim();
                                string ModeID = dtMode.Rows[i]["ID"].ToString().Trim();
                                sql = string.Format("SELECT * FROM v_modedetail WHERE ModeID = '{0}'", ModeID);
                                DataTable dtModeDetail = Common.GetDataTable(sql);
                                if (dtModeDetail.Rows.Count > 0)
                                {
                                    ControlList clData = new ControlList();
                                    clData.GatewayID = GatewayID;
                                    ControlData[] d = new ControlData[dtModeDetail.Rows.Count];
                                    for (int j = 0; j < dtModeDetail.Rows.Count; j++)
                                    {
                                        d[j] = new ControlData();
                                        string DeviceID = dtModeDetail.Rows[j]["DeviceID"].ToString().Trim();
                                        string PropertyName = dtModeDetail.Rows[j]["PropertyName"].ToString().Trim();
                                        string Value = dtModeDetail.Rows[j]["Value"].ToString().Trim();
                                        string Delay = dtModeDetail.Rows[j]["Delay"].ToString().Trim();
                                        string ID = DeviceID + "." + PropertyName;
                                        d[j].ID = ID;
                                        d[j].Value = Value;
                                        d[j].Delay = Delay;
                                    }
                                    clData.Data = d;
                                    Thread thdExecute = new Thread(new ParameterizedThreadStart(ExecuteWork));
                                    thdExecute.IsBackground = true;
                                    thdExecute.Start(clData);
                                }
                            }
                        }
                    }
                }
                catch (Exception E)
                {
                    object[] tmpArray = new object[1];
                    tmpArray[0] = E.Message;
                    this.BeginInvoke(new ProcessErrorMessage(InvokeFunofErrorMessage), tmpArray);
                }
            }
        }

        void mcMain_MqttMsgPublishReceived(object sender, uPLibrary.Networking.M2Mqtt.Messages.MqttMsgPublishEventArgs e)
        {
            try
            {
                if (e.Topic == SUBSCRIBE_STATUS)    //状态数据
                {
                    string s = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);
                    StatusList sl = JsonConvert.DeserializeObject<StatusList>(s);
                    string GatewayID = sl.GatewayID;

                    for (int i = 0; i < sl.Data.Length; i++)
                    {
                        string ID = sl.Data[i].ID;
                        string Value = sl.Data[i].Value;


                        //Encoding.UTF8.GetString(rcMain.Get(ID));

                        rcMain.Set(ID, Encoding.UTF8.GetBytes(Value));


                        object[] tmpArray = new object[3];
                        tmpArray[0] = GatewayID;
                        tmpArray[1] = ID;
                        tmpArray[2] = Value;
                        this.BeginInvoke(new ProcessGatewayData(InvokeFunofGatewayData), tmpArray);
                    }

                }
                else if (e.Topic == SUBSCRIBE_COMMAND)   //控制指令
                {
                    string s = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);
                    ControlList cl = JsonConvert.DeserializeObject<ControlList>(s);
                    //创建线程处理控制指令
                    Thread thdExecute = new Thread(new ParameterizedThreadStart(ExecuteWork));
                    thdExecute.IsBackground = true;
                    thdExecute.Start(cl);

                }
                else if (e.Topic == SUBSCRIBE_REQUEST)   //请求主题
                {
                    string s = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);

                    RequestData rd = JsonConvert.DeserializeObject<RequestData>(s);
                    string Type = rd.Type;
                    string Name = rd.Data.Name;
                    string Value = rd.Data.Value;

                    object[] tmpArray = new object[3];
                    tmpArray[0] = Type;
                    tmpArray[1] = Name;
                    tmpArray[2] = Value;
                    this.BeginInvoke(new ProcessRequestData(InvokeFunofRequestData), tmpArray);

                }
            }
            catch (Exception E)
            {
                object[] tmpArray = new object[1];
                tmpArray[0] = E.Message;
                this.BeginInvoke(new ProcessErrorMessage(InvokeFunofErrorMessage), tmpArray);
            }
        }

        private void InvokeFunofTestMessage(string Message)
        {
            //rtb.Text = Message + "\n" + rtb.Text;
        }

        private void InvokeFunofRequestData(string Type, string Name, string Value)
        {
            try
            {
                if (Type == "REQUESTID")
                {
                    if (Name == "SN")
                    {
                        AddSystemLog("接收到逻辑序列号请求：" + Value);
                        //到数据库中查询逻辑序列号并发送给网关
                        string sql = string.Format("SELECT * FROM gateway WHERE PhysicalID = '{0}'", Value);
                        DataTable dt = Common.GetDataTable(sql);
                        if (dt.Rows.Count > 0)
                        {
                            string ID = dt.Rows[0]["ID"].ToString().Trim();
                            if (ID != string.Empty)
                            {
                                RequestData rd = new RequestData();
                                rd.Type = "REPLYID";
                                Content ct = new Content();
                                ct.Name = "ID";
                                ct.Value = ID;
                                rd.Data = ct;
                                mcMain.Publish(Value, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rd, Newtonsoft.Json.Formatting.Indented)), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);  //将查询到的网关序列号发送给网关
                                AddSystemLog("发送逻辑系列号：" + ID + "【物理序列号" + Value + "】");
                            }
                        }
                        else
                        {
                            AddSystemLog("未查找到逻辑序列号：" + Value);
                        }
                    }
                }
                else if (Type == "REQUESTDEVICE")
                {
                    if (Name == "ID")
                    {
                        AddSystemLog("接收到设备点表请求：" + Value);
                        //到数据库中查询点表信息
                        string sql = string.Format("SELECT * FROM commbus WHERE GatewayID = '{0}'", Value);
                        DataTable dt = Common.GetDataTable(sql);
                        CommBusList cbl = new CommBusList();
                        CommBus[] cb = new CommBus[dt.Rows.Count];
                        for (int i = 0; i < dt.Rows.Count; i++)
                        {
                            cb[i] = new CommBus();
                            cb[i].ID = dt.Rows[i]["ID"].ToString().Trim();
                            cb[i].Name = dt.Rows[i]["Name"].ToString().Trim();
                            cb[i].CommType = dt.Rows[i]["CommType"].ToString().Trim();
                            cb[i].Port = dt.Rows[i]["Port"].ToString().Trim();
                            cb[i].Baudrate = dt.Rows[i]["Baudrate"].ToString().Trim();
                            cb[i].Parity = dt.Rows[i]["Parity"].ToString().Trim();
                            cb[i].DataBits = dt.Rows[i]["DataBits"].ToString().Trim();
                            cb[i].StopBits = dt.Rows[i]["StopBits"].ToString().Trim();
                            cb[i].Handshake = dt.Rows[i]["Handshake"].ToString().Trim();
                            cb[i].IP = dt.Rows[i]["IP"].ToString().Trim();
                            cb[i].IpPort = dt.Rows[i]["IpPort"].ToString().Trim();
                            cb[i].PollingTime = dt.Rows[i]["PollingTime"].ToString().Trim();
                            cb[i].Status = dt.Rows[i]["Status"].ToString().Trim();
                            cb[i].Protocol = dt.Rows[i]["Protocol"].ToString().Trim();
                            sql = string.Format("SELECT * FROM device WHERE CommBusID = '{0}'", cb[i].ID);
                            DataTable dtDevice = Common.GetDataTable(sql);
                            Device[] d = new Device[dtDevice.Rows.Count];
                            for (int j = 0; j < dtDevice.Rows.Count; j++)
                            {
                                d[j] = new Device();
                                d[j].ID = dtDevice.Rows[j]["ID"].ToString().Trim();
                                d[j].Name = dtDevice.Rows[j]["Name"].ToString().Trim();
                                d[j].Type = dtDevice.Rows[j]["Type"].ToString().Trim();
                                d[j].Address = dtDevice.Rows[j]["Address"].ToString().Trim();
                                d[j].SerialNo = dtDevice.Rows[j]["SerialNo"].ToString().Trim();
                                d[j].Protocol = dtDevice.Rows[j]["Protocol"].ToString().Trim();
                                d[j].Params = dtDevice.Rows[j]["Params"].ToString().Trim();
                            }
                            cb[i].Device = d;

                        }
                        cbl.Type = "REPLYDEVICE";
                        cbl.Data = cb;
                        mcMain.Publish(Value + "_device", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(cbl, Newtonsoft.Json.Formatting.Indented)), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);  //将查询到的点表发送给网关
                        AddSystemLog("发送设备点表，物理序列号：" + Value);
                    }
                }
            }
            catch (Exception E)
            {
                AddErrorLog(E.Message);
            }
        }


        private void InvokeFunofErrorMessage(string Message)
        {
            AddErrorLog(Message);
        }

        private void ExecuteWork(object Execute)
        {
            try
            {
                ControlList cl = (ControlList)Execute;
                string GatewayID = cl.GatewayID;
                int intMaxDelay = 0;
                for (int i = 0; i < cl.Data.Length; i++)
                {
                    string Delay = cl.Data[i].Delay;
                    int intDelay = Convert.ToInt32(Delay);
                    if (intDelay > intMaxDelay)
                    {
                        intMaxDelay = intDelay;       //找到最大延时数
                    }
                }
                for (int i = 0; i <= intMaxDelay; i++)
                {
                    for (int j = 0; j < cl.Data.Length; j++)
                    {
                        string ID = cl.Data[j].ID;
                        string Value = cl.Data[j].Value;
                        string Delay = cl.Data[j].Delay;
                        if (Delay == i.ToString())
                        {
                            rcMain.Set(ID, Encoding.UTF8.GetBytes(Value));                                    //更新分布式缓存

                            ControlList clData = new ControlList();
                            clData.GatewayID = GatewayID;
                            ControlData[] d = new ControlData[1];
                            d[0] = new ControlData();
                            d[0].ID = ID;
                            d[0].Value = Value;
                            d[0].Delay = "0";   //马上执行
                            clData.Data = d;

                            mcMain.Publish(GatewayID, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(clData, Newtonsoft.Json.Formatting.Indented)), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);  //将控制数据转发给网关

                            //显示执行记录
                            object[] tmpArray = new object[4];
                            tmpArray[0] = GatewayID;
                            tmpArray[1] = ID;
                            tmpArray[2] = Value;
                            tmpArray[3] = Delay;
                            this.BeginInvoke(new ProcessWebData(InvokeFunofWebData), tmpArray);
                        }
                    }

                    Thread.Sleep(1000);
                }

            }
            catch (Exception E)
            {
                object[] tmpArray = new object[1];
                tmpArray[0] = E.Message;
                this.BeginInvoke(new ProcessErrorMessage(InvokeFunofErrorMessage), tmpArray);
            }
        }

        private void InvokeFunofWebData(string GatewayID, string ID, string Value, string Delay)
        {
            try
            {
                AddWebData(GatewayID, ID, Value, Delay);
                AddDataToDB(GatewayID, ID, Value);            //存储控制数据进数据库
            }
            catch (Exception E)
            {
                AddErrorLog(E.Message);
            }
        }

        private void AddWebData(string GatewayID, string Name, string Value, string Delay)
        {
            ListViewItem lvi;
            ListViewItem.ListViewSubItem lvsi;

            lvi = new ListViewItem();
            lvi.Text = DateTime.Now.ToString();

            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = GatewayID;
            lvi.SubItems.Add(lvsi);

            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = Name;
            lvi.SubItems.Add(lvsi);

            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = Value;
            lvi.SubItems.Add(lvsi);

            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = Delay;
            lvi.SubItems.Add(lvsi);

            lvDownload.Items.Add(lvi);
            if (lvDownload.Items.Count >= 40)
            {
                lvDownload.Items.RemoveAt(0);
            }
        }


        private void InvokeFunofGatewayData(string GatewayID, string ID, string Value)
        {
            try
            {
                AddGatewayData(GatewayID, ID, Value);
                AddDataToDB(GatewayID, ID, Value);
            }
            catch (Exception E)
            {
                AddErrorLog(E.Message);
            }
        }

        private void AddGatewayData(string GatewayID, string Name, string Value)
        {
            ListViewItem lvi;
            ListViewItem.ListViewSubItem lvsi;

            lvi = new ListViewItem();
            lvi.Text = DateTime.Now.ToString();

            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = GatewayID;
            lvi.SubItems.Add(lvsi);

            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = Name;
            lvi.SubItems.Add(lvsi);

            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = Value;
            lvi.SubItems.Add(lvsi);

            lvUpload.Items.Add(lvi);
            if (lvUpload.Items.Count >= 40)
            {
                lvUpload.Items.RemoveAt(0);
            }
        }

        private void AddDataToDB(string GatewayID, string ID, string Value)
        {
            try
            {
                string TableName = GatewayID + "_" + DateTime.Now.ToString("yyyyMM");
                string[] str = ID.Split('.');
                string DeviceID = str[0];
                string PropertyName = str[1];
                string sql = string.Format("INSERT INTO {0}(ID, DeviceID, PropertyName, Value, Time) VALUES('{1}','{2}','{3}','{4}','{5}')", TableName, Common.Guid2String(), DeviceID, PropertyName, Value, DateTime.Now.ToString());
                Common.ExecuteNonQuery(sql);
            }
            catch (MySqlException ex)
            {
                if (ex.Number == 1146)  //表格不存在
                {
                    string TableName = GatewayID + "_" + DateTime.Now.ToString("yyyyMM");
                    string[] str = ID.Split('.');
                    string DeviceID = str[0];
                    string PropertyName = str[1];

                    string sql = string.Format("CREATE TABLE {0}(ID VARCHAR(20) NOT NULL, DeviceID VARCHAR(20) NULL, PropertyName VARCHAR(20) NULL, Value VARCHAR(20) NULL, Time VARCHAR(45) NULL, PRIMARY KEY (ID))", TableName);
                    Common.ExecuteNonQuery(sql);


                    sql = string.Format("INSERT INTO {0}(ID, DeviceID, PropertyName, Value, Time) VALUES('{1}','{2}','{3}','{4}','{5}')", TableName, Common.Guid2String(), DeviceID, PropertyName, Value, DateTime.Now.ToString());
                    Common.ExecuteNonQuery(sql);

                }
                else
                {
                    throw new Exception(ex.ToString());
                }
            }
        }

        private void AddErrorLog(string ErrorMessage)
        {
            ListViewItem lvi;
            ListViewItem.ListViewSubItem lvsi;
            lvi = new ListViewItem();
            lvi.Text = DateTime.Now.ToString();
            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = ErrorMessage;
            lvi.SubItems.Add(lvsi);
            lvErrorLog.Items.Add(lvi);
            if (lvErrorLog.Items.Count >= 40)
            {
                lvErrorLog.Items.RemoveAt(0);
            }
        }

        private void AddSystemLog(string Message)
        {
            ListViewItem lvi;
            ListViewItem.ListViewSubItem lvsi;
            lvi = new ListViewItem();
            lvi.Text = DateTime.Now.ToString();
            lvsi = new ListViewItem.ListViewSubItem();
            lvsi.Text = Message;
            lvi.SubItems.Add(lvsi);
            lvSystemLog.Items.Add(lvi);
            if (lvSystemLog.Items.Count >= 40)
            {
                lvSystemLog.Items.RemoveAt(0);
            }
        }

        private void frmMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (mcMain.IsConnected)
            {
                mcMain.Disconnect();
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(@"c:\update\Version.xml");
                XmlNode xn = xmlDoc.SelectSingleNode("root");
                string Version = xn.Attributes["version"].Value.Trim();
                MessageBox.Show(Version);
                
            }
            catch (Exception E)
            {
                object[] tmpArray = new object[1];
                tmpArray[0] = E.Message;
                this.BeginInvoke(new ProcessErrorMessage(InvokeFunofErrorMessage), tmpArray);
            }
        }
    }
}
