using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.IO.Ports;
using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BltFileTransfer
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {


        public MainWindow()
        {
            InitializeComponent();
            InitialPortName();
            InitialAction();

          
        }

        public DateTime DatetimeRead=DateTime.Now;
        public string FileName = "";

        private void InitialPortName()
        {
            var PortNames = SerialPort.GetPortNames().ToList();
            ComboTxtPortName.ItemsSource = PortNames;
            ComboTxtPortName_R.ItemsSource = PortNames;

            var baudRateList = new List<int>() { 9600,115200, 960000 };
            ComboTxtBaudRate.ItemsSource = baudRateList;
            ComboTxtBaudRate_R.ItemsSource = baudRateList;
            ComboTxtBaudRate.SelectedIndex = 2;
            ComboTxtBaudRate_R.SelectedIndex = 2;

            Progress.Maximum = 100;
        }


        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var path= SelectFileName();
            if (path.Equals(string.Empty)) return;
            using (var port = new SerialPort(ComboTxtPortName.SelectedValue.ToString(), (int)ComboTxtBaudRate.SelectedValue))
            {
                port.Open();
                await SendTask(port,path);
                port.Close();
            }
        }


        private SerialPort ReveievePoer;

        private void BtnReceieve_Click(object sender, RoutedEventArgs e)
        {
            if ((Application.Current.Properties["Status"] == null)|| ((bool)Application.Current.Properties["Status"] == false))
            {
                ReveievePoer = new SerialPort(ComboTxtPortName_R.SelectedValue.ToString(), (int)ComboTxtBaudRate_R.SelectedValue);
                ReveievePoer.DataReceived += DataReceivedHandler;
                ReveievePoer.ReadBufferSize = 10240;
                ReveievePoer.ReadTimeout = 1000;
                ReveievePoer.Open();
                Application.Current.Properties["Status"] = true;

            }
            else
            {
                ReveievePoer.Close();
                ReveievePoer.Dispose();
                Application.Current.Properties["Status"] = false; 
            }
        }

        private void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
        {
            
            if (((DatetimeRead - DateTime.Now).TotalSeconds > 10)|| FileName=="")
            {
                FileName = $"D:\\Transfer{DateTime.Now:yyyy_MM_dd_HH_mm_ss}.zip";
               
            }
            DatetimeRead = DateTime.Now;
            SerialPort sp = (SerialPort)sender;
            var length = sp.BytesToRead;
            byte[] a = new byte[length];
            sp.Read(a, 0, length);
            //var bytes=System.Text.Encoding.UTF8.GetBytes(str);
            if (a.Length == 0) return;
            WriteData(FileName, a);
        }

        private void WriteData(string filepath, byte[] data)
        {
            FileStream fs;
            if (File.Exists(filepath))
            {
                 fs = new FileStream(filepath, FileMode.Append);
            }
            else
            {
                 fs = new FileStream(filepath, FileMode.CreateNew);
            }
           
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(data, 0, data.Length);
            }
            fs.Close();
        }

        private async Task SendTask(SerialPort port, string filepath)
        {
          
           await Task.Run(()=> {
               using (FileStream sr = new FileStream(filepath, FileMode.Open))
               {
                   long fileLength = sr.Length;
                   long fileposition = 0;
                   sr.Position = fileposition;
                   long leftlength = fileLength;
                   byte[] buff;
                   while (leftlength > 0)
                   {
                       Thread.Sleep(10 );
                       if (leftlength > 10240)
                       {
                           buff = new byte[10240];
                           sr.Read(buff, 0, Convert.ToInt32(10240)); 
                           port.Write(buff, 0, 10240);
                           leftlength = leftlength - 10240;
                       }
                       else
                       {
                           buff = new byte[leftlength];
                           sr.Read(buff, 0, (int)leftlength);
                           port.Write(buff, 0, (int)leftlength);
                           leftlength = 0;
                       }
                       this.Dispatcher.Invoke(BindingValue, (fileLength-leftlength) * 100 / fileLength);
                   }
               }
               port.Close();

           });
        }
  
        private string SelectFileName()
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "压缩文件|*.zip;*.jar";
            dialog.CheckFileExists = true;
            dialog.ShowDialog();
            return dialog.FileName;
        }


        private void InitialAction()
        {
            BindingValue = BindingProgressValue;
        }

        private Action<double> BindingValue;


        private void BindingProgressValue(double value)
        {
            Progress.Value = value;
        }


      
   
   
    }

  
}
