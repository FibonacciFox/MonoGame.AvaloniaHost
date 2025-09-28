using System;
using Avalonia.Controls;
using Microsoft.Xna.Framework;


namespace MonoGame.AvaloniaHost.Sample.Views;

public partial class MainWindow : Window
{
 
    public MainWindow()
    {
        InitializeComponent();

        GameHost.Game = new Game1();
        //GameHost1.Game = new Game1();
    }
}
